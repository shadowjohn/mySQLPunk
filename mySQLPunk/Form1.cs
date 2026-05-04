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
        
        // Windows API 用於實作視窗拖拽接管
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;

        public static Dictionary<string, List<object>> displayTools = new Dictionary<string, List<object>>();
        mySQLPunk_main myN = new mySQLPunk_main();
        myinclude my = new myinclude();
        ToolStripButton query_btn = new ToolStripButton(); // 新增查詢按鈕
        private TabControl queryTabs;
        private int queryTabCounter = 1;
        private TabPage dragTab = null; // 用於拖拽頁籤
        private Point dragStartPos;

        // 側邊欄組件
        private Panel pnlSidebar;
        private ToolStrip tsSidebar;
        private RichTextBox rtbDDL;
        private DataGridView dgvDetails;
        private ToolStripButton btnInfo;
        private ToolStripButton btnDDL;
        private Label lblSidebarTitle;
        private ToolStripStatusLabel lblMainStatus; // 新增主程式狀態標籤
        private Panel _dockHintOverlay;
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
        public void UpdateMainStatus(string msg)
        {
            if (lblMainStatus != null)
            {
                lblMainStatus.Text = msg;
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
                TreeNode newNode = new TreeNode(myN.connections[i]["conn_name"].ToString(), i, i);
                switch (myN.connections[i]["db_kind"].ToString())
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
            splitContainer5.Orientation = Orientation.Vertical;
            splitContainer5.SplitterDistance = splitContainer5.Width - 300; 

            // 將 table_top 移到 Panel1 並設為 Fill
            table_top.Parent = splitContainer5.Panel1;
            table_top.Dock = DockStyle.Fill;
            table_top.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None; // 必須先關閉 RowHeader 相關自動調整
            table_top.RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.EnableResizing;
            table_top.RowHeadersVisible = false;
            
            // 確保狀態列在最底端且可見 (保留 Resize 功能)
            statusStrip1.Dock = DockStyle.Bottom;
            statusStrip1.Visible = true; 
            statusStrip1.SizingGrip = true; // 恢復 Resize 手柄
            statusStrip1.SendToBack(); 
            lblMainStatus = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip1.Items.Clear();
            statusStrip1.Items.Add(lblMainStatus);

            // 確保選單與工具列在最頂端
            menuStrip1.SendToBack(); // 根據 WinForms 邏輯，SendToBack 反而是 Dock 最頂端
            tool_Connection.SendToBack(); 
            
            // 確保容器高度足夠容納工具列
            splitContainer1.SplitterDistance = 110;
            tool_Connection.Dock = DockStyle.Fill;
            tool_Connection.AutoSize = false;
            tool_Connection.Height = 110; 
            tool_Connection.ImageScalingSize = new Size(32, 32);
            tool_Connection.GripStyle = ToolStripGripStyle.Hidden;
            tool_Connection.Padding = new Padding(10, 5, 10, 5);

            // 初始化所有頂部工具列按鈕的樣式
            foreach (ToolStripItem item in tool_Connection.Items)
            {
                item.AutoSize = false;
                item.Size = new Size(90, 85);
                item.Padding = new Padding(0, 5, 0, 5); // 內部再加一點邊距
                if (item is ToolStripButton btn) btn.TextImageRelation = TextImageRelation.ImageAboveText;
                if (item is ToolStripDropDownButton dd) dd.TextImageRelation = TextImageRelation.ImageAboveText;
            }

            table_top.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            table_top.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            table_top.AllowUserToAddRows = false;
            table_top.BackgroundColor = Color.White;
            table_top.GridColor = Color.FromArgb(240, 240, 240);
            table_top.BorderStyle = BorderStyle.None;

            // 初始化主狀態標籤
            lblMainStatus = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip1.Items.Add(lblMainStatus);

            queryTabs = new TabControl();
            queryTabs.Dock = DockStyle.Fill;
            queryTabs.Visible = false;
            queryTabs.Parent = splitContainer5.Panel1; // 同樣在 Panel1

            // ── 啟用頁籤自定義繪製與拖拽功能 ──
            queryTabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            queryTabs.Padding = new Point(24, 4); // 留更多空間給 X
            queryTabs.AllowDrop = true; // 支援拖放回巢
            queryTabs.DrawItem += QueryTabs_DrawItem;
            queryTabs.MouseDown += QueryTabs_MouseDown;
            queryTabs.MouseMove += QueryTabs_MouseMove;
            queryTabs.MouseUp += QueryTabs_MouseUp;
            queryTabs.MouseClick += QueryTabs_MouseClick;
            queryTabs.DragEnter += QueryTabs_DragEnter;
            queryTabs.DragDrop += QueryTabs_DragDrop;

            // ── 讓父容器與 table_top 都支援拖放回巢 ──
            splitContainer5.Panel1.AllowDrop = true;
            splitContainer5.Panel1.DragEnter += QueryTabs_DragEnter;
            splitContainer5.Panel1.DragOver += QueryTabs_DragOver;
            splitContainer5.Panel1.DragLeave += QueryTabs_DragLeave;
            splitContainer5.Panel1.DragDrop += QueryTabs_DragDrop;

            // table_top 蓋在 Panel1 上，必須也開 AllowDrop，否則會吃掉 drag 事件
            table_top.AllowDrop = true;
            table_top.DragEnter += QueryTabs_DragEnter;
            table_top.DragOver += QueryTabs_DragOver;
            table_top.DragLeave += QueryTabs_DragLeave;
            table_top.DragDrop += QueryTabs_DragDrop;

            // 初始化 dock 提示框 (懸浮視窗拖回時的視覺指引)
            _dockHintOverlay = new Panel() { Dock = DockStyle.Fill, Visible = false, BackColor = Color.FromArgb(220, 235, 255) };
            _dockHintOverlay.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(0, 120, 212), 4))
                    e.Graphics.DrawRectangle(pen, 2, 2, _dockHintOverlay.Width - 5, _dockHintOverlay.Height - 5);
                const string msg = "釋放滑鼠以嵌入視窗";
                using (var font = new Font("Microsoft JhengHei", 14, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.FromArgb(0, 120, 212)))
                {
                    var sz = e.Graphics.MeasureString(msg, font);
                    e.Graphics.DrawString(msg, font, brush,
                        (_dockHintOverlay.Width - sz.Width) / 2f,
                        (_dockHintOverlay.Height - sz.Height) / 2f);
                }
            };
            splitContainer5.Panel1.Controls.Add(_dockHintOverlay);

            // 初始化側邊欄 (Sidebar) 在 Panel2
            InitSidebar();
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
            query_btn.TextImageRelation = TextImageRelation.ImageAboveText; // 圖示在文字上方
            query_btn.TextAlign = ContentAlignment.BottomCenter;
            query_btn.AutoSize = false;
            query_btn.Size = new Size(85, 85); // 統一按鈕尺寸
            query_btn.Margin = new Padding(2);
            query_btn.Click += Query_btn_Click;
            tool_Connection.Items.Add(query_btn);

            // 連結 Design Table 事件
            DesignTable.Click += DesignTable_Click;

            // 連結右鍵選單事件
            db_tree.NodeMouseClick += db_tree_NodeMouseClick;

            // 嘗試從 image 資料夾載入切好的高解析圖標
            string imgPath = Path.Combine(Application.StartupPath, "image");
            LoadIcon(connection_btn, Path.Combine(imgPath, "connection.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(user_btn, Path.Combine(imgPath, "user.png"), global::mySQLPunk.Properties.Resources.user);
            LoadIcon(table_btn, Path.Combine(imgPath, "table.png"), global::mySQLPunk.Properties.Resources.tables_32);
            LoadIcon(view_btn, Path.Combine(imgPath, "view.png"), global::mySQLPunk.Properties.Resources.views_32);
            // 主工具列樣式調整 (大圖標 + 文字在下)
            tool_Connection.ImageScalingSize = new Size(32, 32);
            tool_Connection.Height = 70;
            tool_Connection.Padding = new Padding(5);

            foreach (ToolStripItem item in tool_Connection.Items)
            {
                item.TextImageRelation = TextImageRelation.ImageAboveText;
                item.TextAlign = ContentAlignment.BottomCenter;
                if (item is ToolStripButton tsb) tsb.AutoSize = false;
                if (item is ToolStripDropDownButton tsddb) tsddb.AutoSize = false;
                item.Size = new Size(70, 65);
            }

            // 初始化導覽列 (Navigation Bar)
            Panel pnlNav = new Panel() { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(245, 245, 245) };
            Label lblNavTitle = new Label() { Name = "lblNav", Text = " Ready", Font = new Font("Microsoft JhengHei", 9), ForeColor = Color.Gray, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            pnlNav.Controls.Add(lblNavTitle);
            this.Controls.Add(pnlNav);
            pnlNav.BringToFront();
            tool_Connection.BringToFront();

            queryTabs.MouseClick += queryTabs_MouseClick;
            table_top.CellMouseDown += table_top_CellMouseDown;
            db_tree.KeyDown += db_tree_KeyDown;

            // 連結工具列事件
            NewTable.Click += (s, e) => CreateNewTable();
            DeleteTable.Click += (s, e) => DeleteSelectedTable();
        }

        private void CreateNewTable()
        {
            if (db_tree.SelectedNode == null) return;
            var pathParts = my.explode("\\", db_tree.SelectedNode.FullPath);
            if (pathParts.Length < 2) return;

            string dbName = pathParts[1];
            TreeNode root = db_tree.SelectedNode;
            while (root.Parent != null) root = root.Parent;
            IDatabase db = (IDatabase)myN.connections[root.Index]["pdo"];

            TableDesignerForm tdf = new TableDesignerForm(db, dbName, "");
            tdf.Text = "New Table";
            DockDockableForm(tdf);
        }

        private void DeleteSelectedTable()
        {
            if (db_tree.SelectedNode == null) return;
            var pathParts = my.explode("\\", db_tree.SelectedNode.FullPath);
            if (pathParts.Length < 4 || pathParts[2] != "Tables") return;

            string dbName = pathParts[1];
            string tableName = pathParts[3];
            
            if (MessageBox.Show($"確定要刪除資料表 「{tableName}」嗎？此操作不可還原！", "警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                TreeNode root = db_tree.SelectedNode;
                while (root.Parent != null) root = root.Parent;
                IDatabase db = (IDatabase)myN.connections[root.Index]["pdo"];

                var res = db.ExecSQL($"DROP TABLE `{dbName}`.`{tableName}`");
                if (res["status"] == "OK")
                {
                    MessageBox.Show("資料表已刪除。");
                    // 重新整理樹狀目錄
                    db_tree.SelectedNode.Parent.Nodes.Remove(db_tree.SelectedNode);
                }
                else MessageBox.Show("刪除失敗：" + res["reason"]);
            }
        }

        private void queryTabs_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                for (int i = 0; i < queryTabs.TabCount; ++i)
                {
                    if (queryTabs.GetTabRect(i).Contains(e.Location))
                    {
                        queryTabs.SelectedIndex = i;
                        var cms = new ContextMenuStrip();
                        var itemFloat = new ToolStripMenuItem("Float / Undock");
                        itemFloat.Click += (s, ev) => {
                            if (queryTabs.SelectedTab?.Tag is IDockableForm dockable)
                                FloatDockableForm(dockable);
                        };
                        cms.Items.Add(itemFloat);
                        
                        var itemClose = new ToolStripMenuItem("Close");
                        itemClose.Click += (s, ev) => {
                            TabPage tp = queryTabs.SelectedTab;
                            if (tp != null)
                            {
                                if (tp.Tag is Form f) f.Close();
                                queryTabs.TabPages.Remove(tp);
                            }
                        };
                        cms.Items.Add(itemClose);

                        cms.Show(queryTabs, e.Location);
                        break;
                    }
                }
            }
        }

        private void InitSidebar()
        {
            pnlSidebar = new Panel() { Dock = DockStyle.Fill, BackColor = Color.White };
            
            // 標題區
            Panel pnlTitle = new Panel() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 5, 10, 0) };
            lblSidebarTitle = new Label() { Text = "Object Details", Font = new Font("Microsoft JhengHei", 10, FontStyle.Bold), AutoSize = true, Location = new Point(10, 10) };
            pnlTitle.Controls.Add(lblSidebarTitle);

            // 切換按鈕 (Info / DDL)
            string imgPath = Path.Combine(Application.StartupPath, "image");
            tsSidebar = new ToolStrip() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.Professional };
            
            btnInfo = new ToolStripButton("Info") { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
            if (File.Exists(Path.Combine(imgPath, "database.png"))) btnInfo.Image = Image.FromFile(Path.Combine(imgPath, "database.png"));
            else btnInfo.Image = global::mySQLPunk.Properties.Resources.database;

            btnDDL = new ToolStripButton("DDL") { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
            if (File.Exists(Path.Combine(imgPath, "queries.png"))) btnDDL.Image = Image.FromFile(Path.Combine(imgPath, "queries.png"));
            else if (File.Exists(Path.Combine(imgPath, "query.png"))) btnDDL.Image = Image.FromFile(Path.Combine(imgPath, "query.png"));
            
            btnInfo.Click += (s, e) => { dgvDetails.Visible = true; rtbDDL.Visible = false; btnInfo.Checked = true; btnDDL.Checked = false; };
            btnDDL.Click += (s, e) => { dgvDetails.Visible = false; rtbDDL.Visible = true; btnInfo.Checked = false; btnDDL.Checked = true; };
            tsSidebar.Items.AddRange(new ToolStripItem[] { btnInfo, btnDDL });
            btnInfo.Checked = true;

            // 內容區 - 詳情表格
            dgvDetails = new DataGridView()
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                ColumnHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false
            };
            dgvDetails.Columns.Add("Key", "Property");
            dgvDetails.Columns.Add("Value", "Value");
            dgvDetails.Columns[0].FillWeight = 40;
            dgvDetails.SelectionMode = DataGridViewSelectionMode.CellSelect;
            dgvDetails.GridColor = Color.FromArgb(245, 245, 245);
            dgvDetails.DefaultCellStyle.Font = new Font("Microsoft JhengHei", 9);

            // 內容區 - DDL
            rtbDDL = new RichTextBox()
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                BackColor = Color.White,
                Font = new Font("Consolas", 10),
                Visible = false
            };

            pnlSidebar.Controls.Add(dgvDetails);
            pnlSidebar.Controls.Add(rtbDDL);
            pnlSidebar.Controls.Add(tsSidebar);
            pnlSidebar.Controls.Add(pnlTitle);

            splitContainer5.Panel2.Controls.Add(pnlSidebar);
            pnlSidebar.BringToFront();
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
                DockDockableForm(tdf);
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
                    string host = connInfo.ContainsKey("host") && connInfo["host"] != null
                        ? connInfo["host"].ToString()
                        : string.Empty;
                    OpenQuery(db, dbName, host, string.Empty, true);
                }
                else
                {
                    MessageBox.Show("請先雙擊連線以開啟資料庫！");
                }
            }
        }

        private void OpenSelectedTableInQuery()
        {
            if (db_tree.SelectedNode == null)
            {
                return;
            }

            TreeNode node = db_tree.SelectedNode;
            var pathParts = my.explode("\\", node.FullPath);

            if (pathParts.Length < 4 || pathParts[2] != "Tables")
            {
                MessageBox.Show("請先在左側選取一個具體的資料表 (Table)！");
                return;
            }

            TreeNode root = node;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            var connInfo = myN.connections[root.Index];
            if (connInfo["isConnect"].ToString() != "T")
            {
                MessageBox.Show("請先雙擊連線以開啟資料庫！");
                return;
            }

            string dbName = pathParts[1];
            string tableName = pathParts[3];
            string host = connInfo.ContainsKey("host") && connInfo["host"] != null
                ? connInfo["host"].ToString()
                : string.Empty;
            string initialSql = "SELECT * FROM `" + dbName + "`.`" + tableName.Replace("`", "``") + "` LIMIT 1000;";

            OpenQuery((IDatabase)connInfo["pdo"], dbName, host, initialSql, true);
        }

        private void OpenQuery(IDatabase db, string dbName, string host, string initialSql, bool docked)
        {
            QueryForm queryForm = new QueryForm(db, dbName, host, initialSql);
            if (docked)
            {
                DockDockableForm(queryForm);
            }
            else
            {
                queryForm.SetMainHost(this);
                queryForm.Show();
            }
        }

        public void DockDockableForm(IDockableForm dockable)
        {
            if (dockable == null) return;
            Form f = (Form)dockable;

            // 檢查是否已經在 Tab 中
            foreach (TabPage tp in queryTabs.TabPages)
            {
                if (tp.Tag == dockable)
                {
                    queryTabs.SelectedTab = tp;
                    return;
                }
            }

            table_top.Visible = false;
            queryTabs.Visible = true;
            queryTabs.BringToFront();

            dockable.SetMainHost(this);
            dockable.PrepareForDocking();

            TabPage page = new TabPage(dockable.GetDisplayTitle());
            page.Tag = dockable;
            f.Dock = DockStyle.Fill;
            page.Controls.Add(f);
            queryTabs.TabPages.Add(page);
            queryTabs.SelectedTab = page;
            f.Show();
        }

        public void FloatDockableForm(IDockableForm dockable)
        {
            if (dockable == null) return;
            Form f = (Form)dockable;

            foreach (TabPage page in queryTabs.TabPages)
            {
                if (page.Tag == dockable)
                {
                    queryTabs.TabPages.Remove(page);
                    break;
                }
            }

            dockable.PrepareForFloating();
            
            // 將視窗中心設定在滑鼠位置
            f.Location = new Point(Cursor.Position.X - f.Width / 2, Cursor.Position.Y - 15);
            f.Show();

            // 強制接管拖拽：讓視窗立刻跟著滑鼠走
            ReleaseCapture();
            SendMessage(f.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);

            if (queryTabs.TabPages.Count == 0)
            {
                queryTabs.Visible = false;
                table_top.Visible = true;
            }
        }

        // 給懸浮視窗的 WndProc 查詢 drop 目標的螢幕範圍
        public Control GetTabDropArea() => splitContainer5.Panel1;

        public void ShowDockHint()
        {
            if (_dockHintOverlay == null) return;
            _dockHintOverlay.Visible = true;
            _dockHintOverlay.BringToFront();
            _dockHintOverlay.Invalidate();
        }

        public void HideDockHint()
        {
            if (_dockHintOverlay != null)
                _dockHintOverlay.Visible = false;
        }

        public void NotifyDockableFormClosed(IDockableForm dockable)
        {
            if (dockable == null || queryTabs == null) return;

            foreach (TabPage page in queryTabs.TabPages)
            {
                if (page.Tag == dockable)
                {
                    queryTabs.TabPages.Remove(page);
                    page.Dispose();
                    break;
                }
            }

            if (queryTabs.TabPages.Count == 0)
            {
                queryTabs.Visible = false;
                table_top.Visible = true;
                table_top.BringToFront();
            }
        }

        private TabPage FindQueryTab(QueryForm queryForm)
        {
            if (queryTabs == null)
            {
                return null;
            }

            foreach (TabPage page in queryTabs.TabPages)
            {
                if (ReferenceEquals(page.Tag, queryForm))
                {
                    return page;
                }
            }

            return null;
        }

        private string BuildQueryTabText(QueryForm queryForm)
        {
            string text = queryForm.GetDisplayTitle();
            if (string.IsNullOrWhiteSpace(text))
            {
                text = "Query " + queryTabCounter;
                queryTabCounter++;
            }

            return text;
        }

        private void DesignSelectedTable()
        {
            DesignTable_Click(this, EventArgs.Empty);
        }

        private void DumpSelectedTableSql(bool structureOnly)
        {
            if (db_tree.SelectedNode == null)
            {
                return;
            }

            TreeNode node = db_tree.SelectedNode;
            var pathParts = my.explode("\\", node.FullPath);

            if (pathParts.Length < 4 || pathParts[2] != "Tables")
            {
                MessageBox.Show("請先在左側選取一個具體的資料表 (Table)！");
                return;
            }

            TreeNode root = node;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            var connInfo = myN.connections[root.Index];
            if (connInfo["isConnect"].ToString() != "T")
            {
                MessageBox.Show("請先雙擊連線以開啟資料庫！");
                return;
            }

            IDatabase db = (IDatabase)connInfo["pdo"];
            if (!(db is my_mysql))
            {
                MessageBox.Show("目前僅支援 MySQL 資料表傾印。", "功能限制", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string dbName = pathParts[1];
            string tableName = pathParts[3];

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "SQL files (*.sql)|*.sql";
                dialog.DefaultExt = "sql";
                dialog.FileName = dbName + "_" + tableName + (structureOnly ? "_schema" : "_data") + ".sql";

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    string sql = BuildMySqlTableDump((my_mysql)db, dbName, tableName, structureOnly);
                    File.WriteAllText(dialog.FileName, sql, Encoding.UTF8);
                    MessageBox.Show("SQL 檔案已匯出。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("匯出 SQL 失敗：" + ex.Message, "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string BuildMySqlTableDump(my_mysql db, string databaseName, string tableName, bool dataOnly)
        {
            string safeDatabaseName = databaseName.Replace("`", "``");
            string safeTableName = tableName.Replace("`", "``");
            StringBuilder builder = new StringBuilder();

            builder.AppendLine("-- mySQLPunk SQL Dump");
            builder.AppendLine("-- Database: `" + safeDatabaseName + "`");
            builder.AppendLine("-- Table: `" + safeTableName + "`");
            builder.AppendLine("SET NAMES utf8mb4;");
            builder.AppendLine("USE `" + safeDatabaseName + "`;");
            builder.AppendLine();

            if (!dataOnly)
            {
                DataTable createTableDt = db.SelectSQL("SHOW CREATE TABLE `" + safeDatabaseName + "`.`" + safeTableName + "`;");
                if (createTableDt.Rows.Count > 0)
                {
                    builder.AppendLine("DROP TABLE IF EXISTS `" + safeTableName + "`;");
                    builder.AppendLine(createTableDt.Rows[0][1].ToString() + ";");
                    builder.AppendLine();
                }
            }

            DataTable dataTable = db.SelectSQL("SELECT * FROM `" + safeDatabaseName + "`.`" + safeTableName + "`;");
            foreach (DataRow row in dataTable.Rows)
            {
                builder.Append("INSERT INTO `");
                builder.Append(safeTableName);
                builder.Append("` VALUES (");

                for (int i = 0; i < dataTable.Columns.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(ToMySqlLiteral(row[i]));
                }

                builder.AppendLine(");");
            }

            return builder.ToString();
        }

        private static string ToMySqlLiteral(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return "NULL";
            }

            if (value is byte[] bytes)
            {
                StringBuilder hex = new StringBuilder(bytes.Length * 2 + 2);
                hex.Append("0x");
                for (int i = 0; i < bytes.Length; i++)
                {
                    hex.Append(bytes[i].ToString("X2"));
                }

                return hex.ToString();
            }

            if (value is bool)
            {
                return ((bool)value) ? "1" : "0";
            }

            if (value is string || value is char || value is DateTime || value is Guid)
            {
                return "'" + value.ToString().Replace("\\", "\\\\").Replace("'", "\\'") + "'";
            }

            if (value is IFormattable formattable)
            {
                return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            }

            return "'" + value.ToString().Replace("\\", "\\\\").Replace("'", "\\'") + "'";
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
            if (e.Node == null) return;

            string fullPath = e.Node.FullPath;
            var pathParts = my.explode("\\", fullPath);

            // 更新導覽列
            Control[] navControls = this.Controls.Find("lblNav", true);
            if (navControls.Length > 0) ((Label)navControls[0]).Text = "  " + fullPath.Replace("\\", " > ");

            // 取得根連線資訊
            TreeNode root = e.Node;
            while (root.Parent != null) root = root.Parent;
            int connIndex = root.Index;
            var connInfo = myN.connections[connIndex];

            if (connInfo["isConnect"].ToString() != "T") return;
            IDatabase db = (IDatabase)connInfo["pdo"];

            if (pathParts.Length >= 2)
            {
                // 選到資料庫及其子節點 (Tables, Table, etc.)
                table_top.Visible = true;
                queryTabs.Visible = false;
                table_top.BringToFront();

                string dbName = pathParts[1];
                
                // 如果是 Database 節點或 Tables 節點，側邊欄標題顯示 DB 名稱
                if (pathParts.Length <= 3)
                {
                    lblSidebarTitle.Text = $"Database: {dbName}";
                    ShowDatabaseObjectList(db, dbName);
                    ShowDatabaseInfo(db, dbName);
                }
                
                // 如果是 Table 節點，則側邊欄顯示 Table 詳情
                if (pathParts.Length >= 4 && pathParts[2] == "Tables")
                {
                    string tableName = pathParts[3];
                    lblSidebarTitle.Text = $"Table: {tableName}";
                    // 只有當 table_top 還沒載入過或是不同 DB 時才重載列表
                    ShowDatabaseObjectList(db, dbName); 
                    ShowTableDetails(db, dbName, tableName);
                }
            }
        }

        private void table_top_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                table_top.ClearSelection();
                table_top.Rows[e.RowIndex].Selected = true;
                
                // 選取對應的 TreeView 節點以保持同步
                string tableName = table_top.Rows[e.RowIndex].Cells["名稱"].Value.ToString();
                SyncTreeWithTable(tableName);

                var cms = new ContextMenuStrip();
                var itemOpen = new ToolStripMenuItem("開啟資料表");
                itemOpen.Click += (s, ev) => OpenSelectedTableInQuery();
                cms.Items.Add(itemOpen);
                
                var itemDesign = new ToolStripMenuItem("設計資料表");
                itemDesign.Click += (s, ev) => DesignSelectedTable();
                cms.Items.Add(itemDesign);

                var itemDrop = new ToolStripMenuItem("刪除資料表");
                itemDrop.Click += (s, ev) => DeleteSelectedTable();
                cms.Items.Add(itemDrop);
                
                cms.Show(table_top, table_top.PointToClient(Cursor.Position));
            }
        }

        private void SyncTreeWithTable(string tableName)
        {
            if (db_tree.SelectedNode == null) return;
            TreeNode parent = db_tree.SelectedNode;
            // 如果選到的是 DB 節點，則找 Tables 節點
            if (parent.Nodes.Count > 0 && parent.Nodes[0].Text == "Tables") parent = parent.Nodes[0];
            // 如果選到的是 Tables 節點，則直接找子節點
            
            foreach (TreeNode node in parent.Nodes)
            {
                if (node.Text == tableName)
                {
                    db_tree.SelectedNode = node;
                    break;
                }
            }
        }

        private void db_tree_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (db_tree.SelectedNode == null) return;

                var pathParts = my.explode("\\", db_tree.SelectedNode.FullPath);
                
                // 使用者要求：table 按 esc 沒事，如果在 database 可以關閉 database (連線)
                if (pathParts.Length <= 2)
                {
                    TreeNode root = db_tree.SelectedNode;
                    while (root.Parent != null) root = root.Parent;
                    CloseConnection(root.Index);
                }
                
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void CloseConnection(int index)
        {
            var conn = myN.connections[index];
            if (conn["isConnect"].ToString() == "T")
            {
                if (conn.ContainsKey("pdo") && conn["pdo"] is IDisposable disp)
                {
                    try { disp.Dispose(); } catch { }
                }
                
                conn["isConnect"] = "F";
                conn["pdo"] = null;
                
                TreeNode root = db_tree.Nodes[index];
                root.Nodes.Clear();
                // 加回一個虛擬節點以便下次雙擊展開
                root.Nodes.Add("loading..."); 
                root.Collapse();
                
                // 重設右側與中間面板
                table_top.DataSource = null;
                table_top.Visible = true;
                queryTabs.Visible = false;
                lblSidebarTitle.Text = "Object Details";
                dgvDetails.DataSource = null;
                rtbDDL.Clear();

                Control[] navControls = this.Controls.Find("lblNav", true);
                if (navControls.Length > 0) ((Label)navControls[0]).Text = " Ready";
                
                MessageBox.Show("連線已斷開。");
            }
        }

        private void ShowDatabaseObjectList(IDatabase db, string dbName)
        {
            try
            {
                DataTable dt = db.GetTableStatus(dbName);
                if (dt == null) return;

                // 轉換為顯示用的格式
                DataTable displayDt = new DataTable();
                displayDt.Columns.Add("名稱");
                displayDt.Columns.Add("自動遞增值");
                displayDt.Columns.Add("修改日期");
                displayDt.Columns.Add("資料長度");
                displayDt.Columns.Add("引擎");
                displayDt.Columns.Add("列");
                displayDt.Columns.Add("註解");

                foreach (DataRow row in dt.Rows)
                {
                    DataRow newRow = displayDt.NewRow();
                    newRow["名稱"] = row["Name"];
                    newRow["自動遞增值"] = row["Auto_increment"];
                    newRow["修改日期"] = row["Update_time"];
                    newRow["資料長度"] = FormatBytes(Convert.ToInt64(row["Data_length"]));
                    newRow["引擎"] = row["Engine"];
                    newRow["列"] = row["Rows"];
                    newRow["註解"] = row["Comment"];
                    displayDt.Rows.Add(newRow);
                }

                table_top.DataSource = displayDt;
            }
            catch { }
        }

        private void ShowDatabaseInfo(IDatabase db, string dbName)
        {
            dgvDetails.Rows.Clear();
            rtbDDL.Text = "";
            btnInfo.PerformClick();

            var info = db.GetDatabaseInfo(dbName);
            if (info.ContainsKey("character_set"))
            {
                dgvDetails.Rows.Add("字符集", info["character_set"]);
                dgvDetails.Rows.Add("定序", info["collation"]);
            }
        }

        private void ShowTableDetails(IDatabase db, string dbName, string tableName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            try
            {
                // 取得詳情
                DataTable dt = db.GetTableStatus(dbName);
                DataRow[] rows = dt.Select($"Name = '{tableName}'");
                if (rows.Length > 0)
                {
                    DataRow r = rows[0];
                    dgvDetails.Rows.Add("列", r["Rows"]);
                    dgvDetails.Rows.Add("引擎", r["Engine"]);
                    dgvDetails.Rows.Add("自動遞增", r["Auto_increment"]);
                    dgvDetails.Rows.Add("列格式", r["Row_format"]);
                    dgvDetails.Rows.Add("修改日期", r["Update_time"]);
                    dgvDetails.Rows.Add("建立日期", r["Create_time"]);
                    dgvDetails.Rows.Add("檢查時間", r["Check_time"]);
                    dgvDetails.Rows.Add("索引長度", FormatBytes(Convert.ToInt64(r["Index_length"])));
                    dgvDetails.Rows.Add("資料長度", FormatBytes(Convert.ToInt64(r["Data_length"])));
                    dgvDetails.Rows.Add("最大資料長度", FormatBytes(Convert.ToInt64(r["Max_data_length"])));
                    dgvDetails.Rows.Add("資料可用空間", FormatBytes(Convert.ToInt64(r["Data_free"])));
                    dgvDetails.Rows.Add("定序", r["Collation"]);
                    dgvDetails.Rows.Add("建立選項", r["Create_options"]);
                    dgvDetails.Rows.Add("註解", r["Comment"]);
                }

                // 取得 DDL
                rtbDDL.Text = db.GetTableCreateStatement(dbName, tableName);
            }
            catch (Exception ex)
            {
                rtbDDL.Text = "Error loading details: " + ex.Message;
            }
        }

        private string FormatBytes(long bytes)
        {
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            if (bytes == 0) return "0 B";
            int i = (int)Math.Floor(Math.Log(Math.Abs(bytes), 1024));
            return Math.Round(bytes / Math.Pow(1024, i), 2) + " " + suffix[i];
        }

        private void db_tree_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                db_tree.SelectedNode = e.Node;

                ContextMenuStrip menu = new ContextMenuStrip();

                if (e.Node.Parent == null)
                {
                    ToolStripMenuItem editItem = new ToolStripMenuItem("編輯連線");
                    editItem.Click += (s, ev) => db_tree_edit_connection(e.Node.Index);
                    menu.Items.Add(editItem);

                    ToolStripMenuItem deleteItem = new ToolStripMenuItem("刪除連線");
                    deleteItem.Click += (s, ev) => db_tree_delete_connection(e.Node.Index);
                    menu.Items.Add(deleteItem);
                }
                else
                {
                    var pathParts = my.explode("\\", e.Node.FullPath);
                    if (pathParts.Length >= 4 && pathParts[2] == "Tables")
                    {
                        ToolStripMenuItem openTableItem = new ToolStripMenuItem("開啟資料表");
                        openTableItem.Click += (s, ev) => OpenSelectedTableInQuery();
                        menu.Items.Add(openTableItem);

                        ToolStripMenuItem designTableItem = new ToolStripMenuItem("設計資料表");
                        designTableItem.Click += (s, ev) => DesignSelectedTable();
                        menu.Items.Add(designTableItem);

                        ToolStripMenuItem dumpSqlItem = new ToolStripMenuItem("傾印 SQL 檔案");
                        ToolStripMenuItem dumpStructureAndDataItem = new ToolStripMenuItem("結構與資料");
                        dumpStructureAndDataItem.Click += (s, ev) => DumpSelectedTableSql(false);
                        dumpSqlItem.DropDownItems.Add(dumpStructureAndDataItem);

                        ToolStripMenuItem dumpDataOnlyItem = new ToolStripMenuItem("僅資料");
                        dumpDataOnlyItem.Click += (s, ev) => DumpSelectedTableSql(true);
                        dumpSqlItem.DropDownItems.Add(dumpDataOnlyItem);

                        menu.Items.Add(dumpSqlItem);
                    }
                }

                if (menu.Items.Count == 0)
                {
                    menu.Dispose();
                    return;
                }

                menu.Show(db_tree, e.Location);
            }
        }

        private void db_tree_edit_connection(int index)
        {
            var conn = myN.connections[index];
            string kind = conn["db_kind"].ToString().ToLower();
            switch (kind)
            {
                case "mysql":
                    {
                        mysql_add_edit form = new mysql_add_edit();
                        form.F1 = this;
                        form.editIndex = index;
                        form.ShowDialog();
                    }
                    break;
                case "postgresql":
                    {
                        postgresql_add_edit form = new postgresql_add_edit();
                        form.F1 = this;
                        form.editIndex = index;
                        form.ShowDialog();
                    }
                    break;
                case "oracle":
                    {
                        oracle_add_edit form = new oracle_add_edit();
                        form.F1 = this;
                        form.editIndex = index;
                        form.oracle_connection_type.Text = "Basic";
                        form.oracle_connection_type_selected_trigger_change();
                        form.ShowDialog();
                    }
                    break;
                default:
                    MessageBox.Show("此連線類型尚未支援編輯：" + kind);
                    break;
            }
        }

        private void db_tree_delete_connection(int index)
        {
            var conn = myN.connections[index];
            string name = conn["conn_name"].ToString();
            var result = MessageBox.Show(
                $"確定要刪除連線「{name}」嗎？",
                "刪除連線",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                myN.connections.RemoveAt(index);
                myN.setSettingINI();
                drawLists();
            }
        }
        private void db_tree_second_click(int father_index, int index, string databaseName)
        {
            //MessageBox.Show(father_index + "," + index+","+databaseName);
            //開啟 database
            switch (myN.connections[father_index]["db_kind"].ToString())
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
            //MessageBox.Show(father_index + "," + index + "," + name);
        }
        public void dialogMyBoxOn(string message, bool can_close)
        {
            dialog.Size = new Size(250, 80);
            dialog.MaximizeBox = false;
            dialog.MinimizeBox = false;
            dialog.AutoSize = true;
            dialog.ControlBox = false;
            dialog.FormBorderStyle = FormBorderStyle.FixedSingle;
            dialog.StartPosition = FormStartPosition.CenterScreen;
            dialog.BackColor = Color.White;
            dialog.ForeColor = Color.FromArgb(51, 51, 51);

            dialogLabel.Location = new Point(0, 0);
            dialogLabel.AutoSize = false;
            dialogLabel.Size = new Size(250, 80);
            dialogLabel.TextAlign = ContentAlignment.MiddleCenter;
            dialogLabel.Text = message;
            dialogLabel.ForeColor = Color.FromArgb(51, 51, 51);
            dialogLabel.Font = new Font("Microsoft JhengHei", 18, FontStyle.Bold);
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
            string fullPath = tree.SelectedNode.FullPath;
            var m = my.explode("\\", fullPath);

            // 取得根連線索引 (永遠以最頂層節點為準)
            TreeNode rootNode = tree.SelectedNode;
            while (rootNode.Parent != null) rootNode = rootNode.Parent;
            int index = rootNode.Index;

            if (m.Length == 2)
            {
                // 代表是資料庫層級
                db_tree_second_click(index, tree.SelectedNode.Index, tree.SelectedNode.Text);
                dialogMyBoxOff();
                return;
            }
            if (m.Length == 3)
            {
                // 代表是 Tables/Views 群組層級
                db_tree_third_click(index, tree.SelectedNode.Parent.Index, tree.SelectedNode.Parent.Text, tree.SelectedNode.Text);
                dialogMyBoxOff();
                return;
            }
            if (m.Length == 4)
            {
                // 代表是資料表層級 -> 直接開啟
                OpenSelectedTableInQuery();
                dialogMyBoxOff();
                return;
            }

            // 如果是根連線節點 (m.Length == 1)
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
                switch (db["db_kind"].ToString().ToLower())
                {
                    case "postgresql":
                        {
                            //Server=127.0.0.1;Port=5432;Database=myDataBase;User Id=myUsername;
                            //Password = myPassword;
                            myN.connections[index]["connString"] = "Server=" + myN.connections[index]["host"].ToString() + ";" +
                               "Port=" + myN.connections[index]["port"].ToString() + ";" +
                               "User Id=" + myN.connections[index]["username"].ToString() + ";" +
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
                            myN.connections[index]["connString"] = "server=" + myN.connections[index]["host"].ToString() + ";" +
                                "port=" + myN.connections[index]["port"].ToString() + ";" +
                                "user id=" + myN.connections[index]["username"].ToString() + ";" +
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
                            myN.connections[index]["connString"] = "Data Source=" + myN.connections[index]["host"].ToString() + "," + myN.connections[index]["port"].ToString() + "; " +
                                // + "," + myN.connections[index]["port"].ToString() + "
                                "Integrated Security=True;" +
                                "Initial Catalog=master;" +
                                "User ID=" + myN.connections[index]["username"].ToString() + ";" +
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

        public Dictionary<string, object> get_connection(int index)
        {
            return myN.connections[index];
        }

        public void update_connection(int index, Dictionary<string, object> conn)
        {
            myN.connections[index] = conn;
            myN.setSettingINI();
            drawLists();
        }
        // ── 頁籤進階功能實作 ──

        private void QueryTabs_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= queryTabs.TabPages.Count) return;
            
            var tabRect = queryTabs.GetTabRect(e.Index);
            var title = queryTabs.TabPages[e.Index].Text;
            bool isSelected = (queryTabs.SelectedIndex == e.Index);
            
            // 背景
            Color bgColor = isSelected ? Color.White : Color.FromArgb(240, 240, 240);
            e.Graphics.FillRectangle(new SolidBrush(bgColor), tabRect);

            // 文字 (置中偏左)
            Rectangle textRect = new Rectangle(tabRect.X + 4, tabRect.Y, tabRect.Width - 25, tabRect.Height);
            TextRenderer.DrawText(e.Graphics, title, queryTabs.Font, textRect, Color.Black, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            // 繪製關閉按鈕 (X)
            var xRect = GetCloseButtonRect(tabRect);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Color.Gray, 1.5f))
            {
                // 畫兩條線組成 X
                e.Graphics.DrawLine(p, xRect.X + 3, xRect.Y + 3, xRect.Right - 3, xRect.Bottom - 3);
                e.Graphics.DrawLine(p, xRect.Right - 3, xRect.Y + 3, xRect.X + 3, xRect.Bottom - 3);
            }
        }

        private Rectangle GetCloseButtonRect(Rectangle tabRect)
        {
            return new Rectangle(tabRect.Right - 20, tabRect.Top + (tabRect.Height - 14) / 2, 14, 14);
        }

        private void QueryTabs_MouseClick(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < queryTabs.TabPages.Count; i++)
            {
                var tabRect = queryTabs.GetTabRect(i);
                
                if ((e.Button == MouseButtons.Middle && tabRect.Contains(e.Location)) ||
                    (e.Button == MouseButtons.Left && GetCloseButtonRect(tabRect).Contains(e.Location)))
                {
                    // ── 增加關閉前確認邏輯 ──
                    TabPage tp = queryTabs.TabPages[i];
                    var designer = tp.Controls.OfType<TableDesignerForm>().FirstOrDefault();
                    if (designer != null)
                    {
                        if (!designer.ConfirmClose()) return; // 取消關閉
                    }
                    
                    queryTabs.TabPages.RemoveAt(i);
                    if (queryTabs.TabPages.Count == 0) queryTabs.Visible = false;
                    break;
                }
            }
        }

        private void QueryTabs_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragTab = GetTabAt(e.Location);
                dragStartPos = e.Location;
            }
        }

        private void QueryTabs_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || dragTab == null) return;
            
            // 只有移動超過一定距離才算拖拽
            if (Math.Abs(e.X - dragStartPos.X) < 5 && Math.Abs(e.Y - dragStartPos.Y) < 5) return;

            // 檢查是否移出範圍 (拖拽拉出功能)
            if (!queryTabs.ClientRectangle.Contains(e.Location))
            {
                var dockable = dragTab.Controls.OfType<Form>().OfType<IDockableForm>().FirstOrDefault();
                if (dockable != null)
                {
                    dragTab = null; // 停止拖拽
                    FloatDockableForm(dockable);
                    return;
                }
            }

            TabPage hoverTab = GetTabAt(e.Location);
            if (hoverTab != null && hoverTab != dragTab)
            {
                int dragIdx = queryTabs.TabPages.IndexOf(dragTab);
                int dropIdx = queryTabs.TabPages.IndexOf(hoverTab);
                
                queryTabs.TabPages.RemoveAt(dragIdx);
                queryTabs.TabPages.Insert(dropIdx, dragTab);
                queryTabs.SelectedTab = dragTab;
            }
        }

        // 從 DragDrop 資料中取出任何實作 IDockableForm 的物件
        private IDockableForm GetDockableFromDrag(IDataObject data)
        {
            foreach (string fmt in data.GetFormats(false))
            {
                try
                {
                    var obj = data.GetData(fmt, false);
                    if (obj is IDockableForm dock) return dock;
                }
                catch { }
            }
            return null;
        }

        private void QueryTabs_DragEnter(object sender, DragEventArgs e)
        {
            if (GetDockableFromDrag(e.Data) != null)
            {
                e.Effect = DragDropEffects.Move;
                queryTabs.Visible = true;
                queryTabs.BackColor = Color.FromArgb(200, 230, 255);
                if (queryTabs.TabPages.Count == 0)
                {
                    statusStrip1.Visible = true; // 拖入時顯示提示
                    lblMainStatus.Text = "Release mouse to dock the window...";
                }
            }
            else
                e.Effect = DragDropEffects.None;
        }

        private void QueryTabs_DragOver(object sender, DragEventArgs e)
        {
            if (GetDockableFromDrag(e.Data) != null)
                e.Effect = DragDropEffects.Move;
        }

        private void QueryTabs_DragLeave(object sender, EventArgs e)
        {
            queryTabs.BackColor = Color.White;
            if (queryTabs.TabPages.Count == 0) queryTabs.Visible = false;
            statusStrip1.Visible = false; // 離開時隱藏
            lblMainStatus.Text = "Ready";
        }

        private void QueryTabs_DragDrop(object sender, DragEventArgs e)
        {
            queryTabs.BackColor = Color.White;
            statusStrip1.Visible = false; // 放下時隱藏
            lblMainStatus.Text = "Ready";
            var dockable = GetDockableFromDrag(e.Data);
            if (dockable != null)
                DockDockableForm(dockable);
        }

        private void QueryTabs_MouseUp(object sender, MouseEventArgs e)
        {
            dragTab = null;
        }

        private TabPage GetTabAt(Point position)
        {
            for (int i = 0; i < queryTabs.TabPages.Count; i++)
            {
                if (queryTabs.GetTabRect(i).Contains(position)) return queryTabs.TabPages[i];
            }
            return null;
        }
        public void UpdateTabTitle(Form childForm)
        {
            foreach (TabPage tp in queryTabs.TabPages)
            {
                if (tp.Controls.Contains(childForm))
                {
                    tp.Text = childForm.Text;
                    break;
                }
            }
        }
    }
}
