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
        ToolStripButton function_btn = new ToolStripButton();
        ToolStripButton other_btn = new ToolStripButton();
        ToolStripButton query_section_btn = new ToolStripButton();
        ToolStripButton backup_btn = new ToolStripButton();
        ToolStripButton auto_run_btn = new ToolStripButton();
        ToolStripButton model_btn = new ToolStripButton();
        ToolStripButton bi_btn = new ToolStripButton();
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
        private DatabaseCopyItem _treeClipboardItem;
        private bool _allowTreeLabelEdit = false;

        private class TreeDatabaseTarget
        {
            public IDatabase Database;
            public string DatabaseName;
            public string ProviderName;
            public TreeNode DatabaseNode;
            public Dictionary<string, object> ConnectionInfo;
        }

        private class DatabaseObjectSelection
        {
            public IDatabase Database;
            public string DatabaseName;
            public string ObjectName;
            public string GroupName;
            public string Host;
            public TreeNode Node;
        }

        private class OpenQueryTabInfo
        {
            public int DisplayIndex;
            public int TabIndex;
            public TabPage Page;
            public QueryForm Query;
        }

        private static readonly string[] DatabaseReportNames =
        {
            "Database Summary",
            "Table Row Counts",
            "Object Inventory"
        };

        public Form1()
        {
            Localization.Load();
            ThemeManager.Load();
            InitializeComponent();
        }
        
        public static void ApplyModernTheme(Control parent)
        {
            if (parent is Form f)
            {
                f.Opacity = 1.0f;
                f.KeyPreview = true;
                f.KeyDown += (s, e) => {
                    if (e.KeyCode == Keys.Escape) f.Close();
                };
            }
            ThemeManager.ApplyTo(parent);
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
                case "點到View本身":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "views")
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
                case "點到Function本身":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "functions")
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
            }
        }
        public void UpdateMainStatus(string msg)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(UpdateMainStatus), msg);
                return;
            }
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
            btns.Add(function_btn);
            btns.Add(user_btn);
            btns.Add(other_btn);
            btns.Add(query_section_btn);
            btns.Add(backup_btn);
            btns.Add(auto_run_btn);
            btns.Add(model_btn);
            btns.Add(bi_btn);
            for (int i = 0, max_i = btns.Count; i < max_i; i++)
            {
                ((ToolStripButton)btns[i]).Checked = false;
                ((ToolStripButton)btns[i]).BackColor = ThemeManager.SurfaceColor;
            }
            switch (kind.ToLower())
            {
                case "user":
                    user_btn.BackColor = ThemeManager.SelectionColor;
                    user_btn.Checked = true;
                    break;
                case "table":
                    table_btn.BackColor = ThemeManager.SelectionColor;
                    table_btn.Checked = true;
                    break;
                case "view":
                    view_btn.BackColor = ThemeManager.SelectionColor;
                    view_btn.Checked = true;
                    break;
                case "function":
                    function_btn.BackColor = ThemeManager.SelectionColor;
                    function_btn.Checked = true;
                    break;
                case "other":
                    other_btn.BackColor = ThemeManager.SelectionColor;
                    other_btn.Checked = true;
                    break;
                case "query":
                    query_section_btn.BackColor = ThemeManager.SelectionColor;
                    query_section_btn.Checked = true;
                    break;
                case "backup":
                    backup_btn.BackColor = ThemeManager.SelectionColor;
                    backup_btn.Checked = true;
                    break;
                case "auto":
                    auto_run_btn.BackColor = ThemeManager.SelectionColor;
                    auto_run_btn.Checked = true;
                    break;
                case "model":
                    model_btn.BackColor = ThemeManager.SelectionColor;
                    model_btn.Checked = true;
                    break;
                case "bi":
                    bi_btn.BackColor = ThemeManager.SelectionColor;
                    bi_btn.Checked = true;
                    break;
            }
        }
        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string message =
                "mySQLPunk\r\n\r\n" +
                "版本：" + Application.ProductVersion + "\r\n" +
                "平台：.NET Framework WinForms\r\n" +
                "支援連線：MySQL、PostgreSQL、SQLite、SQL Server、Oracle";
            MessageBox.Show(message, Localization.T("Menu.Help"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void toolStripMenuItem2_Click(object sender, EventArgs e)
        {

        }

        private void OpenConnectionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TreeNode root = GetSelectedConnectionRoot();
            if (root == null)
            {
                UpdateMainStatus(Localization.T("Status.SelectConnection"));
                return;
            }

            db_tree.SelectedNode = root;
            db_tree_DoubleClick(db_tree, EventArgs.Empty);
        }

        private void CloseConnectionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TreeNode root = GetSelectedConnectionRoot();
            if (root == null)
            {
                UpdateMainStatus(Localization.T("Status.SelectConnection"));
                return;
            }

            CloseConnection(root.Index);
        }

        private void ExportToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = Localization.T("Connection.ExportTitle");
                dialog.Filter = Localization.T("Connection.JsonFilter");
                dialog.FileName = "mysqlpunk-connections.json";
                dialog.DefaultExt = "json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    myN.exportConnections(dialog.FileName);
                    UpdateMainStatus(Localization.T("Status.ConnectionsExported"));
                    MessageBox.Show(Localization.T("Status.ConnectionsExported"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.T("Status.ExportFailed") + ex.Message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ImportConnectionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = Localization.T("Connection.ImportTitle");
                dialog.Filter = Localization.T("Connection.JsonFilter");
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                DialogResult answer = MessageBox.Show(
                    Localization.T("Connection.ImportReplaceConfirm"),
                    Localization.T("Common.Confirm"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;

                try
                {
                    myN.importConnections(dialog.FileName);
                    drawLists();
                    UpdateMainStatus(Localization.T("Status.ConnectionsImported"));
                    MessageBox.Show(Localization.T("Status.ConnectionsImported"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.T("Status.ImportFailed") + ex.Message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void CloseToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CloseSelectedTab();
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

            ConfigureMainMenu();

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
            lblMainStatus = new ToolStripStatusLabel(Localization.T("Status.Ready")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip1.Items.Clear();
            statusStrip1.Items.Add(lblMainStatus);

            // 確保上方只有兩排：選單列 + 圖示工具列。
            menuStrip1.Dock = DockStyle.Top;
            menuStrip1.Visible = true;
            menuStrip1.BringToFront();
            
            // 確保容器高度足夠容納工具列
            splitContainer1.SplitterDistance = 86;
            tool_Connection.Dock = DockStyle.Fill;
            tool_Connection.AutoSize = false;
            tool_Connection.Height = 86;
            tool_Connection.ImageScalingSize = new Size(32, 32);
            tool_Connection.GripStyle = ToolStripGripStyle.Hidden;
            tool_Connection.Padding = new Padding(10, 8, 10, 4);

            // 初始化所有頂部工具列按鈕的樣式
            foreach (ToolStripItem item in tool_Connection.Items)
            {
                item.AutoSize = false;
                item.Size = new Size(72, 74);
                item.Padding = new Padding(0, 4, 0, 2);
                if (item is ToolStripButton btn) btn.TextImageRelation = TextImageRelation.ImageAboveText;
                if (item is ToolStripDropDownButton dd) dd.TextImageRelation = TextImageRelation.ImageAboveText;
            }
            Resize -= Form1_Resize;
            Resize += Form1_Resize;
            ArrangeMainLayout();

            table_top.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            table_top.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            table_top.AllowUserToAddRows = false;
            table_top.BackgroundColor = Color.White;
            table_top.GridColor = Color.FromArgb(240, 240, 240);
            table_top.BorderStyle = BorderStyle.None;

            // 初始化主狀態標籤
            lblMainStatus = new ToolStripStatusLabel(Localization.T("Status.Ready")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
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

            // ── 父容器與 table_top 只負責轉送拖放事件，真正可 dock 的範圍會限制在頁籤列 ──
            splitContainer5.Panel1.AllowDrop = true;
            splitContainer5.Panel1.DragEnter += QueryTabs_DragEnter;
            splitContainer5.Panel1.DragOver += QueryTabs_DragOver;
            splitContainer5.Panel1.DragLeave += QueryTabs_DragLeave;
            splitContainer5.Panel1.DragDrop += QueryTabs_DragDrop;

            // table_top 蓋在 Panel1 上，必須也開 AllowDrop；但內容區不會觸發 dock
            table_top.AllowDrop = true;
            table_top.DragEnter += QueryTabs_DragEnter;
            table_top.DragOver += QueryTabs_DragOver;
            table_top.DragLeave += QueryTabs_DragLeave;
            table_top.DragDrop += QueryTabs_DragDrop;

            // 初始化 dock 提示框 (懸浮視窗拖回時的視覺指引)
            _dockHintOverlay = new Panel() { Dock = DockStyle.Top, Height = 36, Visible = false, BackColor = Color.FromArgb(220, 235, 255) };
            _dockHintOverlay.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(0, 120, 212), 4))
                    e.Graphics.DrawRectangle(pen, 2, 2, _dockHintOverlay.Width - 5, _dockHintOverlay.Height - 5);
                string msg = Localization.T("Status.ReleaseToDock");
                using (var font = new Font("Microsoft JhengHei", 14, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.FromArgb(0, 120, 212)))
                {
                    var sz = e.Graphics.MeasureString(msg, font);
                    e.Graphics.DrawString(msg, font, brush,
                        12f,
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

            string imgPath = Path.Combine(Application.StartupPath, "image");
            ConfigureMainToolbar(imgPath);
            ApplyLanguage();

            // 連結第二層工具列事件
            OpenTable.Click += (s, e) => OpenSelectedTableInQuery();
            DesignTable.Click += DesignTable_Click;
            NewTable.Click += (s, e) => CreateNewTable();
            DeleteTable.Click += (s, e) => DeleteSelectedTable();
            ImportWizard.Click += (s, e) => ImportSqlWithDialog();
            ExportWizard.Click += (s, e) => DumpSelectedTableSql(false);
            OpenView.Click += (s, e) => OpenSelectedViewInQuery();
            DesignView.Click += (s, e) => ShowSelectedViewDefinition();
            NewView.Click += (s, e) => CreateNewView();
            DeleteView.Click += (s, e) => DeleteSelectedView();
            View_ExportWizard.Click += (s, e) => DumpSelectedViewSql();
            DesignFunction.Click += (s, e) => ShowSelectedFunctionDefinition();
            NewFunction.Click += (s, e) => CreateNewFunction();
            DeleteFunction.Click += (s, e) => DeleteSelectedFunction();
            ExecuteFunction.Click += (s, e) => ExecuteSelectedFunction();

            // 連結右鍵選單事件
            db_tree.NodeMouseClick += db_tree_NodeMouseClick;

            tool_Connection.BringToFront();

            queryTabs.MouseClick += queryTabs_MouseClick;
            table_top.CellMouseDown += table_top_CellMouseDown;
            table_top.CellDoubleClick += table_top_CellDoubleClick;
            db_tree.KeyDown += db_tree_KeyDown;
            db_tree.LabelEdit = true;
            db_tree.BeforeLabelEdit += db_tree_BeforeLabelEdit;
            db_tree.AfterLabelEdit += db_tree_AfterLabelEdit;
            sQLiteToolStripMenuItem.Click += sQLiteToolStripMenuItem_Click;
            sQLServerToolStripMenuItem.Click -= sQLServerToolStripMenuItem_Click;
            sQLServerToolStripMenuItem.Click += sQLServerToolStripMenuItem_Click;
            newConnectionToolStripMenuItem.Click -= NewConnectionToolStripMenuItem_Click;
            newConnectionToolStripMenuItem.Click += NewConnectionToolStripMenuItem_Click;
            openConnectionToolStripMenuItem.Click -= OpenConnectionToolStripMenuItem_Click;
            openConnectionToolStripMenuItem.Click += OpenConnectionToolStripMenuItem_Click;
            closeConnectionToolStripMenuItem.Click -= CloseConnectionToolStripMenuItem_Click;
            closeConnectionToolStripMenuItem.Click += CloseConnectionToolStripMenuItem_Click;
            exportToolStripMenuItem.Click -= ExportToolStripMenuItem_Click;
            exportToolStripMenuItem.Click += ExportToolStripMenuItem_Click;
            importConnectionsToolStripMenuItem.Click -= ImportConnectionsToolStripMenuItem_Click;
            importConnectionsToolStripMenuItem.Click += ImportConnectionsToolStripMenuItem_Click;
            closeToolStripMenuItem.Click -= CloseToolStripMenuItem_Click;
            closeToolStripMenuItem.Click += CloseToolStripMenuItem_Click;

        }

        private void ConfigureMainMenu()
        {
            檔案ToolStripMenuItem.Text = Localization.T("Menu.File");
            newConnectionToolStripMenuItem.Text = Localization.T("Menu.NewConnection");
            openConnectionToolStripMenuItem.Text = Localization.T("Menu.OpenConnection");
            closeConnectionToolStripMenuItem.Text = Localization.T("Menu.CloseConnection");
            exportToolStripMenuItem.Text = Localization.T("Menu.ExportConnections");
            importConnectionsToolStripMenuItem.Text = Localization.T("Menu.ImportConnections");
            closeToolStripMenuItem.Text = Localization.T("Menu.Close");
            exitToolStripMenuItem.Text = Localization.T("Menu.Exit");
            ttToolStripMenuItem.Text = Localization.T("Menu.Window");
            helpToolStripMenuItem.Text = Localization.T("Menu.Help");
            aboutToolStripMenuItem.Text = Localization.T("Menu.About");

            ToolStripMenuItem editMenu = new ToolStripMenuItem(Localization.T("Menu.Edit"));
            ToolStripMenuItem viewMenu = new ToolStripMenuItem(Localization.T("Menu.View"));
            ToolStripMenuItem favoriteMenu = new ToolStripMenuItem(Localization.T("Menu.Favorites"));
            ToolStripMenuItem toolsMenu = new ToolStripMenuItem(Localization.T("Menu.Tools"));
            ToolStripMenuItem languageMenu = new ToolStripMenuItem(Localization.T("Menu.Language"));
            ToolStripMenuItem zhMenu = new ToolStripMenuItem(Localization.T("Menu.LanguageZh"));
            ToolStripMenuItem enMenu = new ToolStripMenuItem(Localization.T("Menu.LanguageEn"));
            ToolStripMenuItem themeMenu = new ToolStripMenuItem(Localization.T("Menu.Theme"));
            ToolStripMenuItem lightMenu = new ToolStripMenuItem(Localization.T("Menu.ThemeLight"));
            ToolStripMenuItem darkMenu = new ToolStripMenuItem(Localization.T("Menu.ThemeDark"));
            ToolStripMenuItem optionsMenu = new ToolStripMenuItem(Localization.T("Menu.Options"));
            zhMenu.Checked = !Localization.IsEnglish;
            enMenu.Checked = Localization.IsEnglish;
            zhMenu.Click += (s, e) => ApplyPreferences(Localization.TraditionalChinese, ThemeManager.CurrentTheme, "Status.LanguageChanged");
            enMenu.Click += (s, e) => ApplyPreferences(Localization.English, ThemeManager.CurrentTheme, "Status.LanguageChanged");
            languageMenu.DropDownItems.AddRange(new ToolStripItem[] { zhMenu, enMenu });
            lightMenu.Checked = !ThemeManager.IsDark;
            darkMenu.Checked = ThemeManager.IsDark;
            lightMenu.Click += (s, e) => ApplyPreferences(Localization.CurrentLanguage, ThemeManager.Light, "Status.ThemeChanged");
            darkMenu.Click += (s, e) => ApplyPreferences(Localization.CurrentLanguage, ThemeManager.Dark, "Status.ThemeChanged");
            themeMenu.DropDownItems.AddRange(new ToolStripItem[] { lightMenu, darkMenu });
            optionsMenu.Click += (s, e) => OpenOptionsDialog();
            toolsMenu.DropDownItems.AddRange(new ToolStripItem[] { optionsMenu, new ToolStripSeparator(), languageMenu, themeMenu });

            menuStrip1.Items.Clear();
            menuStrip1.Items.AddRange(new ToolStripItem[]
            {
                檔案ToolStripMenuItem,
                editMenu,
                viewMenu,
                favoriteMenu,
                toolsMenu,
                ttToolStripMenuItem,
                helpToolStripMenuItem
            });
        }

        private void OpenOptionsDialog()
        {
            using (OptionsForm form = new OptionsForm())
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    ApplyPreferences(form.SelectedLanguage, form.SelectedTheme, "Status.OptionsApplied");
                }
            }
        }

        private void ApplyPreferences(string language, string theme, string statusKey)
        {
            Localization.SetLanguage(language, true);
            ThemeManager.SetTheme(theme, true);
            ApplyLanguage();
            foreach (Form form in Application.OpenForms)
            {
                if (form == this) continue;
                QueryForm queryForm = form as QueryForm;
                if (queryForm != null) queryForm.ApplyLanguage();
                TableDesignerForm designerForm = form as TableDesignerForm;
                if (designerForm != null) designerForm.ApplyLanguage();
                Localization.ApplyTo(form);
                if (queryForm != null) queryForm.ApplyTheme();
                if (designerForm != null) designerForm.ApplyTheme();
                ThemeManager.ApplyTo(form);
            }
            UpdateMainStatus(Localization.T(statusKey));
        }

        private void ApplyLanguage()
        {
            Text = Localization.T("App.Title");
            ConfigureMainMenu();
            ConfigureMainToolbar(Path.Combine(Application.StartupPath, "image"));
            label1.Text = Localization.T("Sidebar.Connections");
            if (lblSidebarTitle != null) lblSidebarTitle.Text = Localization.T("Sidebar.ObjectDetails");
            if (lblMainStatus != null && (lblMainStatus.Text == "Ready" || lblMainStatus.Text == "就緒" || string.IsNullOrWhiteSpace(lblMainStatus.Text)))
            {
                lblMainStatus.Text = Localization.T("Status.Ready");
            }

            OpenTable.Text = Localization.T("Tool.OpenTable");
            DesignTable.Text = Localization.T("Tool.DesignTable");
            NewTable.Text = Localization.T("Tool.NewTable");
            DeleteTable.Text = Localization.T("Tool.DeleteTable");
            ImportWizard.Text = Localization.T("Tool.ImportWizard");
            ExportWizard.Text = Localization.T("Tool.ExportWizard");
            OpenView.Text = Localization.T("Tool.OpenView");
            DesignView.Text = Localization.T("Tool.DesignView");
            NewView.Text = Localization.T("Tool.NewView");
            DeleteView.Text = Localization.T("Tool.DeleteView");
            View_ExportWizard.Text = Localization.T("Tool.ExportWizard");
            DesignFunction.Text = Localization.T("Tool.DesignFunction");
            NewFunction.Text = Localization.T("Tool.NewFunction");
            DeleteFunction.Text = Localization.T("Tool.DeleteFunction");
            ExecuteFunction.Text = Localization.T("Tool.ExecuteFunction");
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            ThemeManager.ApplyTo(this);
            if (_dockHintOverlay != null)
            {
                _dockHintOverlay.BackColor = ThemeManager.SelectionColor;
                _dockHintOverlay.Invalidate();
            }
            if (table_top != null)
            {
                table_top.BackgroundColor = ThemeManager.WindowBackColor;
                table_top.GridColor = ThemeManager.GridColor;
            }
            if (pnlSidebar != null) pnlSidebar.BackColor = ThemeManager.WindowBackColor;
            if (rtbDDL != null)
            {
                rtbDDL.BackColor = ThemeManager.TextBoxBackColor;
                rtbDDL.ForeColor = ThemeManager.TextColor;
            }
            if (lblSidebarTitle != null) lblSidebarTitle.ForeColor = ThemeManager.TextColor;
            if (lblMainStatus != null) lblMainStatus.ForeColor = ThemeManager.TextColor;
            ThemeManager.ApplyToolStrip(menuStrip1);
            ThemeManager.ApplyToolStrip(tool_Connection);
            ThemeManager.ApplyToolStrip(statusStrip1);
            if (tsSidebar != null) ThemeManager.ApplyToolStrip(tsSidebar);
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
            if (pathParts.Length < 4 || pathParts[2] != "Tables")
            {
                MessageBox.Show("請先在左側選取一個具體的資料表 (Table)！");
                return;
            }

            string dbName = pathParts[1];
            string tableName = pathParts[3];
            
            if (MessageBox.Show($"確定要刪除資料表 「{tableName}」嗎？此操作不可還原！", "警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                TreeNode root = db_tree.SelectedNode;
                while (root.Parent != null) root = root.Parent;
                IDatabase db = (IDatabase)myN.connections[root.Index]["pdo"];

                var res = db.ExecSQL("DROP TABLE " + BuildQualifiedObjectName(db, dbName, tableName));
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
                        var itemFloat = new ToolStripMenuItem(Localization.T("Query.Float") + " / Undock");
                        itemFloat.Click += (s, ev) => {
                            if (queryTabs.SelectedTab?.Tag is IDockableForm dockable)
                                FloatDockableForm(dockable);
                        };
                        cms.Items.Add(itemFloat);
                        
                        var itemClose = new ToolStripMenuItem(Localization.T("Menu.Close"));
                        itemClose.Click += (s, ev) => {
                            TabPage tp = queryTabs.SelectedTab;
                            if (tp != null)
                            {
                                if (tp.Tag is Form f) f.Close();
                                queryTabs.TabPages.Remove(tp);
                            }
                        };
                        cms.Items.Add(itemClose);

                        ThemeManager.ApplyToolStrip(cms);
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
            lblSidebarTitle = new Label() { Text = Localization.T("Sidebar.ObjectDetails"), Font = new Font("Microsoft JhengHei", 10, FontStyle.Bold), AutoSize = true, Location = new Point(10, 10) };
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

        private void ConfigureMainToolbar(string imgPath)
        {
            ConfigureToolbarItem(connection_btn, Localization.T("Toolbar.Connection"));
            ConfigureToolbarItem(query_btn, Localization.T("Toolbar.NewQuery"));
            ConfigureToolbarItem(table_btn, Localization.T("Toolbar.Table"));
            ConfigureToolbarItem(view_btn, Localization.T("Toolbar.View"));
            ConfigureToolbarItem(function_btn, Localization.T("Toolbar.Function"));
            ConfigureToolbarItem(user_btn, Localization.T("Toolbar.User"));
            ConfigureToolbarItem(other_btn, Localization.T("Toolbar.Other"));
            ConfigureToolbarItem(query_section_btn, Localization.T("Toolbar.Query"));
            ConfigureToolbarItem(backup_btn, Localization.T("Toolbar.Backup"));
            ConfigureToolbarItem(auto_run_btn, Localization.T("Toolbar.AutoRun"));
            ConfigureToolbarItem(model_btn, Localization.T("Toolbar.Model"));
            ConfigureToolbarItem(bi_btn, Localization.T("Toolbar.BI"));

            query_btn.Click -= Query_btn_Click;
            query_btn.Click += Query_btn_Click;
            function_btn.Click -= function_btn_Click;
            function_btn.Click += function_btn_Click;
            other_btn.Click -= other_btn_Click;
            other_btn.Click += other_btn_Click;
            query_section_btn.Click -= query_section_btn_Click;
            query_section_btn.Click += query_section_btn_Click;
            backup_btn.Click -= backup_btn_Click;
            backup_btn.Click += backup_btn_Click;
            auto_run_btn.Click -= auto_run_btn_Click;
            auto_run_btn.Click += auto_run_btn_Click;
            model_btn.Click -= model_btn_Click;
            model_btn.Click += model_btn_Click;
            bi_btn.Click -= bi_btn_Click;
            bi_btn.Click += bi_btn_Click;
            connection_btn.Click -= connection_btn_Click;
            connection_btn.Click += connection_btn_Click;
            connection_btn.ShowDropDownArrow = false;
            connection_btn.DropDownItems.Clear();

            LoadIcon(connection_btn, Path.Combine(imgPath, "connection.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(query_btn, Path.Combine(imgPath, "new_query.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(table_btn, Path.Combine(imgPath, "table.png"), global::mySQLPunk.Properties.Resources.tables_32);
            LoadIcon(view_btn, Path.Combine(imgPath, "view.png"), global::mySQLPunk.Properties.Resources.views_32);
            LoadIcon(function_btn, Path.Combine(imgPath, "functions.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(user_btn, Path.Combine(imgPath, "user.png"), global::mySQLPunk.Properties.Resources.user);
            LoadIcon(other_btn, Path.Combine(imgPath, "other.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(query_section_btn, Path.Combine(imgPath, "query_section.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(backup_btn, Path.Combine(imgPath, "backups.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(auto_run_btn, Path.Combine(imgPath, "auto_run.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(model_btn, Path.Combine(imgPath, "model.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(bi_btn, Path.Combine(imgPath, "bi.png"), global::mySQLPunk.Properties.Resources.database);

            tool_Connection.Items.Clear();
            tool_Connection.Items.AddRange(new ToolStripItem[]
            {
                connection_btn,
                query_btn,
                table_btn,
                view_btn,
                function_btn,
                user_btn,
                other_btn,
                query_section_btn,
                backup_btn,
                auto_run_btn,
                model_btn,
                bi_btn
            });

            tool_Connection.ImageScalingSize = new Size(32, 32);
            tool_Connection.Height = 86;
            tool_Connection.Padding = new Padding(10, 8, 10, 4);

            foreach (ToolStripItem item in tool_Connection.Items)
            {
                item.TextImageRelation = TextImageRelation.ImageAboveText;
                item.TextAlign = ContentAlignment.BottomCenter;
                item.AutoSize = false;
                item.Size = new Size(72, 74);
                item.Margin = new Padding(2, 2, 2, 0);
                item.Padding = new Padding(0, 4, 0, 2);
            }
        }

        private static void ConfigureToolbarItem(ToolStripItem item, string text)
        {
            item.Text = text;
            item.AutoToolTip = false;
            item.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            item.ImageScaling = ToolStripItemImageScaling.None;
        }

        private void LoadIcon(ToolStripItem btn, string path, Image defaultImg)
        {
            if (File.Exists(path))
            {
                try 
                { 
                    using (Image image = Image.FromFile(path))
                    {
                        btn.Image = CreateToolbarIcon(image);
                    }
                    btn.ImageScaling = ToolStripItemImageScaling.None;
                }
                catch { btn.Image = CreateToolbarIcon(defaultImg); }
            }
            else
            {
                btn.Image = CreateToolbarIcon(defaultImg);
            }
        }

        private static Image CreateToolbarIcon(Image source)
        {
            Bitmap bitmap = new Bitmap(32, 32);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                float scale = Math.Min(30f / source.Width, 30f / source.Height);
                int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                int height = Math.Max(1, (int)Math.Round(source.Height * scale));
                int x = (32 - width) / 2;
                int y = (32 - height) / 2;
                graphics.DrawImage(source, new Rectangle(x, y, width, height));
            }

            return bitmap;
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
            string initialSql = "SELECT * FROM " + QuoteDumpIdentifier((IDatabase)connInfo["pdo"], tableName) + ";";

            OpenQuery((IDatabase)connInfo["pdo"], dbName, host, initialSql, true);
        }

        private void OpenSelectedViewInQuery()
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Views")
            {
                MessageBox.Show("請先在左側選取一個具體的檢視 (View)！");
                return;
            }

            string initialSql = "SELECT * FROM " + QuoteDumpIdentifier(selection.Database, selection.ObjectName) + ";";
            OpenQuery(selection.Database, selection.DatabaseName, selection.Host, initialSql, true);
        }

        private DatabaseObjectSelection GetSelectedDatabaseObject()
        {
            if (db_tree.SelectedNode == null) return null;

            TreeNode node = db_tree.SelectedNode;
            var pathParts = my.explode("\\", node.FullPath);
            if (pathParts.Length < 4) return null;
            if (pathParts[2] != "Tables" && pathParts[2] != "Views" && pathParts[2] != "Functions") return null;

            TreeNode root = node;
            while (root.Parent != null) root = root.Parent;
            var connInfo = myN.connections[root.Index];
            if (connInfo["isConnect"].ToString() != "T" || !(connInfo["pdo"] is IDatabase db)) return null;

            return new DatabaseObjectSelection
            {
                Database = db,
                DatabaseName = pathParts[1],
                ObjectName = pathParts[3],
                GroupName = pathParts[2],
                Host = connInfo.ContainsKey("host") && connInfo["host"] != null ? connInfo["host"].ToString() : string.Empty,
                Node = node
            };
        }

        private void ShowSelectedViewDefinition()
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Views")
            {
                MessageBox.Show("請先在左側選取一個具體的檢視 (View)！");
                return;
            }

            lblSidebarTitle.Text = "View: " + selection.ObjectName;
            ShowDatabaseGroupList(selection.Database, selection.DatabaseName, "Views");
            ShowViewDetails(selection.Database, selection.DatabaseName, selection.ObjectName);
            btnDDL.PerformClick();
        }

        private void CreateNewView()
        {
            TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
            if (target == null)
            {
                MessageBox.Show("請先選取一個已展開的資料庫或 Views 節點。", "新增檢視", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string initialSql = "CREATE VIEW " + QuoteDumpIdentifier(target.Database, "new_view") + " AS" + Environment.NewLine +
                                "SELECT *" + Environment.NewLine +
                                "FROM " + QuoteDumpIdentifier(target.Database, "table_name") + ";";
            OpenQuery(target.Database, target.DatabaseName, string.Empty, initialSql, true);
            UpdateMainStatus("New view SQL template opened.");
        }

        private void CreateNewFunction()
        {
            TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
            if (target == null)
            {
                MessageBox.Show("請先選取一個已展開的資料庫或 Functions 節點。", "新增函式", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            OpenQuery(target.Database, target.DatabaseName, GetTargetHost(target), BuildFunctionTemplate(target.Database, target.DatabaseName), true);
            UpdateMainStatus("New function SQL template opened.");
        }

        private void ShowSelectedFunctionDefinition()
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Functions")
            {
                MessageBox.Show("請先在左側選取一個具體的函式或程序 (Function/Procedure)！");
                return;
            }

            lblSidebarTitle.Text = "Function: " + selection.ObjectName;
            ShowDatabaseGroupList(selection.Database, selection.DatabaseName, "Functions");
            ShowFunctionDetails(selection.Database, selection.DatabaseName, selection.ObjectName);
            btnDDL.PerformClick();
        }

        private void ExecuteSelectedFunction()
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Functions")
            {
                MessageBox.Show("請先在左側選取一個具體的函式或程序 (Function/Procedure)！");
                return;
            }

            string initialSql = BuildFunctionExecuteSql(selection.Database, selection.DatabaseName, selection.ObjectName, GetSelectedFunctionType(selection));
            OpenQuery(selection.Database, selection.DatabaseName, selection.Host, initialSql, true);
            UpdateMainStatus("Function execution SQL opened: " + selection.ObjectName);
        }

        private void DeleteSelectedFunction()
        {
            DropSelectedFunction(true);
        }

        private bool DropSelectedFunction(bool confirm)
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Functions")
            {
                MessageBox.Show("請先在左側選取一個具體的函式或程序 (Function/Procedure)！");
                return false;
            }

            if (selection.Database is my_sqlite)
            {
                MessageBox.Show("SQLite 不支援資料庫內建 stored function。", "刪除函式", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            if (confirm && MessageBox.Show($"確定要刪除函式或程序「{selection.ObjectName}」嗎？此操作不可還原！", "警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return false;
            }

            string routineType = GetSelectedFunctionType(selection);
            string dropKind = IsProcedureRoutine(routineType) ? "PROCEDURE" : "FUNCTION";
            Dictionary<string, string> res = selection.Database.ExecSQL("DROP " + dropKind + " " + BuildQualifiedObjectName(selection.Database, selection.DatabaseName, selection.ObjectName));
            if (res.ContainsKey("status") && res["status"] == "OK")
            {
                TreeNode parent = db_tree.SelectedNode.Parent;
                parent.Nodes.Remove(db_tree.SelectedNode);
                db_tree.SelectedNode = parent;
                ShowDatabaseGroupList(selection.Database, selection.DatabaseName, "Functions");
                UpdateMainStatus("Function deleted: " + selection.ObjectName);
                if (confirm) MessageBox.Show("函式或程序已刪除。");
                return true;
            }

            MessageBox.Show("刪除失敗：" + (res.ContainsKey("reason") ? res["reason"] : "unknown error"));
            return false;
        }

        private void DeleteSelectedView()
        {
            DropSelectedView(true);
        }

        private bool DropSelectedView(bool confirm)
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Views")
            {
                MessageBox.Show("請先在左側選取一個具體的檢視 (View)！");
                return false;
            }

            if (confirm && MessageBox.Show($"確定要刪除檢視 「{selection.ObjectName}」嗎？此操作不可還原！", "警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return false;
            }

            var res = selection.Database.ExecSQL("DROP VIEW " + BuildQualifiedObjectName(selection.Database, selection.DatabaseName, selection.ObjectName));
            if (res.ContainsKey("status") && res["status"] == "OK")
            {
                TreeNode parent = db_tree.SelectedNode.Parent;
                parent.Nodes.Remove(db_tree.SelectedNode);
                db_tree.SelectedNode = parent;
                ShowDatabaseGroupList(selection.Database, selection.DatabaseName, "Views");
                UpdateMainStatus("View deleted: " + selection.ObjectName);
                if (confirm) MessageBox.Show("檢視已刪除。");
                return true;
            }
            else
            {
                MessageBox.Show("刪除失敗：" + (res.ContainsKey("reason") ? res["reason"] : "unknown error"));
                return false;
            }
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
            RefreshQueriesGroupIfSelected();
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
        public Control GetTabDropArea() => queryTabs;

        public Rectangle GetTabDropScreenBounds()
        {
            if (queryTabs == null || splitContainer5 == null) return Rectangle.Empty;

            int headerHeight = 34;
            if (queryTabs.TabPages.Count > 0)
            {
                Rectangle firstTab = queryTabs.GetTabRect(0);
                if (!firstTab.IsEmpty)
                    headerHeight = Math.Max(headerHeight, firstTab.Bottom + 6);
            }

            Rectangle hostBounds = queryTabs.Visible
                ? queryTabs.RectangleToScreen(queryTabs.ClientRectangle)
                : splitContainer5.Panel1.RectangleToScreen(splitContainer5.Panel1.ClientRectangle);

            // 只允許頁籤列區域觸發 dock，避免整個內容畫面都變成吸附區。
            return new Rectangle(hostBounds.Left, hostBounds.Top, hostBounds.Width, headerHeight);
        }

        public bool IsPointInTabDropArea(Point screenPoint)
        {
            Rectangle bounds = GetTabDropScreenBounds();
            return !bounds.IsEmpty && bounds.Contains(screenPoint);
        }

        public void ShowDockHint()
        {
            if (_dockHintOverlay == null) return;
            Rectangle bounds = GetTabDropScreenBounds();
            if (!bounds.IsEmpty)
                _dockHintOverlay.Height = bounds.Height;
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
            RefreshQueriesGroupIfSelected();
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
                    string sql = BuildTableDump(db, dbName, tableName, structureOnly);
                    File.WriteAllText(dialog.FileName, sql, Encoding.UTF8);
                    MessageBox.Show("SQL 檔案已匯出。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("匯出 SQL 失敗：" + ex.Message, "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void DumpSelectedViewSql()
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Views")
            {
                MessageBox.Show("請先在左側選取一個具體的檢視 (View)！");
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "SQL files (*.sql)|*.sql";
                dialog.DefaultExt = "sql";
                dialog.FileName = selection.DatabaseName + "_" + selection.ObjectName + "_view.sql";

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    string sql = BuildViewDump(selection.Database, selection.DatabaseName, selection.ObjectName);
                    File.WriteAllText(dialog.FileName, sql, Encoding.UTF8);
                    MessageBox.Show("SQL 檔案已匯出。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("匯出 SQL 失敗：" + ex.Message, "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ImportSqlWithDialog()
        {
            TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
            if (target == null)
            {
                MessageBox.Show(Localization.T("ImportSql.SelectDatabase"), Localization.T("ImportSql.Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = Localization.T("ImportSql.Title");
                dialog.Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*";
                dialog.Multiselect = false;

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    string script = File.ReadAllText(dialog.FileName, Encoding.UTF8);
                    int executed = ImportSqlScript(target, script);
                    string message = string.Format(Localization.T("ImportSql.Success"), executed);
                    MessageBox.Show(message, Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateMainStatus(message);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.T("ImportSql.Failed") + ex.Message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateMainStatus("Import SQL failed: " + ex.Message);
                }
            }
        }

        private int ImportSqlScriptToSelectedDatabase(string script)
        {
            TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
            if (target == null)
            {
                return -1;
            }

            return ImportSqlScript(target, script);
        }

        private int ImportSqlScript(TreeDatabaseTarget target, string script)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            int executed = ExecuteSqlScript(target.Database, target.DatabaseName, script);
            RefreshDatabaseObjectNodes(target.DatabaseNode);
            db_tree.SelectedNode = target.DatabaseNode;
            UpdateMainStatus(string.Format(Localization.T("ImportSql.Success"), executed));
            return executed;
        }

        private static int ExecuteSqlScript(IDatabase db, string databaseName, string script)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (script == null) throw new ArgumentNullException(nameof(script));

            int executed = 0;
            foreach (string statement in SplitSqlScript(script))
            {
                string sql = statement.Trim();
                if (sql.Length == 0) continue;

                Dictionary<string, string> result = db.ExecSQL(sql);
                if (!result.ContainsKey("status") || result["status"] != "OK")
                {
                    string reason = result.ContainsKey("reason") ? result["reason"] : "unknown error";
                    throw new Exception(reason + Environment.NewLine + sql);
                }

                executed++;
            }

            return executed;
        }

        private static List<string> SplitSqlScript(string script)
        {
            List<string> statements = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inSingle = false;
            bool inDouble = false;
            bool inBacktick = false;
            bool inLineComment = false;
            bool inBlockComment = false;

            for (int i = 0; i < script.Length; i++)
            {
                char c = script[i];
                char next = i + 1 < script.Length ? script[i + 1] : '\0';

                if (inLineComment)
                {
                    current.Append(c);
                    if (c == '\n') inLineComment = false;
                    continue;
                }

                if (inBlockComment)
                {
                    current.Append(c);
                    if (c == '*' && next == '/')
                    {
                        current.Append(next);
                        i++;
                        inBlockComment = false;
                    }
                    continue;
                }

                if (!inSingle && !inDouble && !inBacktick)
                {
                    if (c == '-' && next == '-')
                    {
                        current.Append(c);
                        current.Append(next);
                        i++;
                        inLineComment = true;
                        continue;
                    }
                    if (c == '/' && next == '*')
                    {
                        current.Append(c);
                        current.Append(next);
                        i++;
                        inBlockComment = true;
                        continue;
                    }
                    if (c == ';')
                    {
                        statements.Add(current.ToString());
                        current.Clear();
                        continue;
                    }
                }

                current.Append(c);

                if (c == '\'' && !inDouble && !inBacktick)
                {
                    if (inSingle && next == '\'')
                    {
                        current.Append(next);
                        i++;
                    }
                    else
                    {
                        inSingle = !inSingle;
                    }
                }
                else if (c == '"' && !inSingle && !inBacktick)
                {
                    if (inDouble && next == '"')
                    {
                        current.Append(next);
                        i++;
                    }
                    else
                    {
                        inDouble = !inDouble;
                    }
                }
                else if (c == '`' && !inSingle && !inDouble)
                {
                    inBacktick = !inBacktick;
                }
            }

            if (current.Length > 0)
            {
                statements.Add(current.ToString());
            }

            return statements;
        }

        private void BackupSelectedDatabaseWithDialog()
        {
            TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
            if (target == null)
            {
                MessageBox.Show(Localization.T("Backup.SelectDatabase"), Localization.T("Backup.Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                bool sqlite = target.Database is my_sqlite;
                dialog.Filter = sqlite ? "SQLite database (*.sqlite)|*.sqlite|All files (*.*)|*.*" : "SQL files (*.sql)|*.sql|All files (*.*)|*.*";
                dialog.DefaultExt = sqlite ? "sqlite" : "sql";
                dialog.FileName = target.DatabaseName + "_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + (sqlite ? ".sqlite" : ".sql");

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    CreateDatabaseBackup(target, dialog.FileName);
                    MessageBox.Show(Localization.T("Backup.Success"), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateMainStatus("Backup created: " + dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.T("Backup.Failed") + ex.Message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateMainStatus("Backup failed: " + ex.Message);
                }
            }
        }

        private bool BackupSelectedDatabaseToFile(string targetPath)
        {
            TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
            if (target == null)
            {
                return false;
            }

            CreateDatabaseBackup(target, targetPath);
            UpdateMainStatus("Backup created: " + targetPath);
            return true;
        }

        private void CreateDatabaseBackup(TreeDatabaseTarget target, string targetPath)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Target path is required.", nameof(targetPath));

            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            my_sqlite sqlite = target.Database as my_sqlite;
            if (sqlite != null)
            {
                using (System.Data.SQLite.SQLiteConnection destination = new System.Data.SQLite.SQLiteConnection("Data Source=" + targetPath + ";Version=3;"))
                {
                    destination.Open();
                    sqlite.MCT.BackupDatabase(destination, "main", "main", -1, null, 0);
                }
                return;
            }

            File.WriteAllText(targetPath, BuildDatabaseDump(target.Database, target.DatabaseName), Encoding.UTF8);
        }

        private static string BuildDatabaseDump(IDatabase db, string databaseName)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("-- mySQLPunk Database Backup");
            builder.AppendLine("-- Provider: " + db.ProviderName);
            builder.AppendLine("-- Database: " + databaseName);
            builder.AppendLine("-- Created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine();

            foreach (string tableName in db.GetTables(databaseName))
            {
                builder.AppendLine(BuildTableDump(db, databaseName, tableName, false));
            }

            foreach (string viewName in db.GetViews(databaseName))
            {
                builder.AppendLine(BuildViewDump(db, databaseName, viewName));
            }

            return builder.ToString();
        }

        private static string BuildTableDump(IDatabase db, string databaseName, string tableName, bool dataOnly)
        {
            StringBuilder builder = new StringBuilder();
            string quotedTable = QuoteDumpIdentifier(db, tableName);

            builder.AppendLine("-- mySQLPunk SQL Dump");
            builder.AppendLine("-- Provider: " + db.ProviderName);
            builder.AppendLine("-- Database: " + databaseName);
            builder.AppendLine("-- Table: " + tableName);
            if (db is my_mysql)
            {
                builder.AppendLine("SET NAMES utf8mb4;");
                builder.AppendLine("USE " + QuoteDumpIdentifier(db, databaseName) + ";");
            }
            builder.AppendLine();

            if (!dataOnly)
            {
                string ddl = db.GetTableCreateStatement(databaseName, tableName);
                if (!string.IsNullOrWhiteSpace(ddl))
                {
                    builder.AppendLine(ddl.TrimEnd().TrimEnd(';') + ";");
                    builder.AppendLine();
                }
            }

            long total = db.CountRows(databaseName, tableName);
            long copied = 0;
            const int batchSize = 1000;
            while (copied < total)
            {
                DataTable dataTable = db.SelectTablePage(databaseName, tableName, copied, batchSize);
                if (dataTable == null || dataTable.Rows.Count == 0) break;

                foreach (DataRow row in dataTable.Rows)
                {
                    builder.Append("INSERT INTO ");
                    builder.Append(quotedTable);
                    builder.Append(" (");

                    for (int i = 0; i < dataTable.Columns.Count; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        builder.Append(QuoteDumpIdentifier(db, dataTable.Columns[i].ColumnName));
                    }

                    builder.Append(") VALUES (");

                    for (int i = 0; i < dataTable.Columns.Count; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        builder.Append(ToSqlLiteral(row[i]));
                    }

                    builder.AppendLine(");");
                }

                copied += dataTable.Rows.Count;
            }

            return builder.ToString();
        }

        private static string BuildViewDump(IDatabase db, string databaseName, string viewName)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("-- mySQLPunk SQL Dump");
            builder.AppendLine("-- Provider: " + db.ProviderName);
            builder.AppendLine("-- Database: " + databaseName);
            builder.AppendLine("-- View: " + viewName);
            if (db is my_mysql)
            {
                builder.AppendLine("SET NAMES utf8mb4;");
                builder.AppendLine("USE " + QuoteDumpIdentifier(db, databaseName) + ";");
            }
            builder.AppendLine();

            string ddl = db.GetViewCreateStatement(databaseName, viewName);
            if (!string.IsNullOrWhiteSpace(ddl))
            {
                builder.AppendLine(ddl.TrimEnd().TrimEnd(';') + ";");
            }

            return builder.ToString();
        }

        private static string QuoteDumpIdentifier(IDatabase db, string name)
        {
            if (db is my_mysql)
            {
                return "`" + name.Replace("`", "``") + "`";
            }
            if (db is my_mssql)
            {
                return "[" + name.Replace("]", "]]") + "]";
            }
            if (db is my_postgresql || db is my_sqlite || db is my_oracle)
            {
                return "\"" + name.Replace("\"", "\"\"") + "\"";
            }
            return name;
        }

        private static string BuildQualifiedObjectName(IDatabase db, string databaseName, string objectName)
        {
            if (db is my_mysql)
            {
                return QuoteDumpIdentifier(db, databaseName) + "." + QuoteDumpIdentifier(db, objectName);
            }
            if (db is my_mssql)
            {
                return QuoteDumpIdentifier(db, databaseName) + ".[dbo]." + QuoteDumpIdentifier(db, objectName);
            }
            if (db is my_postgresql)
            {
                return "\"public\"." + QuoteDumpIdentifier(db, objectName);
            }
            if (db is my_sqlite)
            {
                return QuoteDumpIdentifier(db, objectName);
            }
            if (db is my_oracle)
            {
                return QuoteDumpIdentifier(db, databaseName) + "." + QuoteDumpIdentifier(db, objectName);
            }
            return QuoteDumpIdentifier(db, objectName);
        }

        private static string BuildFunctionTemplate(IDatabase db, string databaseName)
        {
            if (db is my_sqlite)
            {
                return "-- SQLite does not store functions in the database schema." + Environment.NewLine +
                       "-- Application-defined SQLite functions must be registered by the client connection." + Environment.NewLine +
                       "SELECT 1;";
            }

            if (db is my_mysql)
            {
                return "DELIMITER $$" + Environment.NewLine +
                       "CREATE FUNCTION " + QuoteDumpIdentifier(db, "new_function") + "()" + Environment.NewLine +
                       "RETURNS INT" + Environment.NewLine +
                       "DETERMINISTIC" + Environment.NewLine +
                       "BEGIN" + Environment.NewLine +
                       "    RETURN 1;" + Environment.NewLine +
                       "END$$" + Environment.NewLine +
                       "DELIMITER ;";
            }

            if (db is my_postgresql)
            {
                return "CREATE OR REPLACE FUNCTION \"public\"." + QuoteDumpIdentifier(db, "new_function") + "()" + Environment.NewLine +
                       "RETURNS integer" + Environment.NewLine +
                       "LANGUAGE sql" + Environment.NewLine +
                       "AS $$" + Environment.NewLine +
                       "    SELECT 1;" + Environment.NewLine +
                       "$$;";
            }

            if (db is my_mssql)
            {
                return "CREATE FUNCTION [dbo]." + QuoteDumpIdentifier(db, "new_function") + "()" + Environment.NewLine +
                       "RETURNS INT" + Environment.NewLine +
                       "AS" + Environment.NewLine +
                       "BEGIN" + Environment.NewLine +
                       "    RETURN 1;" + Environment.NewLine +
                       "END;";
            }

            if (db is my_oracle)
            {
                string owner = string.IsNullOrWhiteSpace(databaseName) ? "USER" : QuoteDumpIdentifier(db, databaseName.ToUpperInvariant());
                return "CREATE OR REPLACE FUNCTION " + owner + "." + QuoteDumpIdentifier(db, "NEW_FUNCTION") + Environment.NewLine +
                       "RETURN NUMBER" + Environment.NewLine +
                       "AS" + Environment.NewLine +
                       "BEGIN" + Environment.NewLine +
                       "    RETURN 1;" + Environment.NewLine +
                       "END;" + Environment.NewLine +
                       "/";
            }

            return "CREATE FUNCTION new_function() RETURNS INT BEGIN RETURN 1; END;";
        }

        private static string BuildFunctionExecuteSql(IDatabase db, string databaseName, string routineName, string routineType)
        {
            bool isProcedure = IsProcedureRoutine(routineType);
            string qualifiedName = BuildQualifiedObjectName(db, databaseName, routineName);

            if (db is my_mssql)
            {
                return isProcedure ? "EXEC " + qualifiedName + ";" : "SELECT " + qualifiedName + "();";
            }

            if (db is my_oracle)
            {
                return isProcedure ? "BEGIN" + Environment.NewLine + "    " + qualifiedName + "();" + Environment.NewLine + "END;" :
                    "SELECT " + qualifiedName + "() FROM dual;";
            }

            if (isProcedure)
            {
                return "CALL " + qualifiedName + "();";
            }

            return "SELECT " + qualifiedName + "();";
        }

        private static bool IsProcedureRoutine(string routineType)
        {
            return !string.IsNullOrWhiteSpace(routineType) &&
                   routineType.IndexOf("procedure", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string GetSelectedFunctionType(DatabaseObjectSelection selection)
        {
            if (selection == null) return "Function";

            DataRow match = GetDatabaseFunctions(selection.Database, selection.DatabaseName).Rows
                .Cast<DataRow>()
                .FirstOrDefault(row => string.Equals(row["Name"].ToString(), selection.ObjectName, StringComparison.OrdinalIgnoreCase));
            return match == null ? "Function" : match["Type"].ToString();
        }

        private static string GetTargetHost(TreeDatabaseTarget target)
        {
            if (target == null || target.ConnectionInfo == null || !target.ConnectionInfo.ContainsKey("host") || target.ConnectionInfo["host"] == null)
            {
                return string.Empty;
            }

            return target.ConnectionInfo["host"].ToString();
        }

        private static string ToSqlLiteral(object value)
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
                return "'" + value.ToString().Replace("'", "''") + "'";
            }

            if (value is IFormattable formattable)
            {
                return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            }

            return "'" + value.ToString().Replace("'", "''") + "'";
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            ApplyModernTheme(this);
            UI_init();
            myN.getSettingINI();
            drawLists();
            ArrangeMainLayout();
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            ArrangeMainLayout();
        }

        private void ArrangeMainLayout()
        {
            if (menuStrip1 == null || splitContainer1 == null || statusStrip1 == null) return;

            int menuHeight = menuStrip1.Visible ? menuStrip1.Height : 0;
            int statusHeight = statusStrip1.Visible ? statusStrip1.Height : 0;

            menuStrip1.Dock = DockStyle.None;
            menuStrip1.AutoSize = false;
            menuStrip1.BackColor = ThemeManager.SurfaceColor;
            menuStrip1.Location = new Point(0, 0);
            menuStrip1.Size = new Size(ClientSize.Width, menuHeight);

            statusStrip1.Dock = DockStyle.None;
            statusStrip1.AutoSize = false;
            statusStrip1.BackColor = ThemeManager.SurfaceColor;
            statusStrip1.Location = new Point(0, Math.Max(menuHeight, ClientSize.Height - statusHeight));
            statusStrip1.Size = new Size(ClientSize.Width, statusHeight);
            statusStrip1.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            splitContainer1.Dock = DockStyle.None;
            splitContainer1.Location = new Point(0, menuHeight);
            splitContainer1.Size = new Size(
                ClientSize.Width,
                Math.Max(0, ClientSize.Height - menuHeight - statusHeight));
            splitContainer1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            int toolbarHeight = Math.Min(86, Math.Max(0, splitContainer1.Height - splitContainer1.SplitterWidth));
            if (toolbarHeight > 0 && splitContainer1.SplitterDistance != toolbarHeight)
            {
                splitContainer1.SplitterDistance = toolbarHeight;
            }

            menuStrip1.BringToFront();
            statusStrip1.BringToFront();
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
                
                // 如果是 Database 節點或物件分類節點，依分類顯示清單
                if (pathParts.Length == 2)
                {
                    lblSidebarTitle.Text = $"Database: {dbName}";
                    ShowDatabaseObjectList(db, dbName);
                    ShowDatabaseInfo(db, dbName);
                    showTools("點到展開的資料庫");
                }
                else if (pathParts.Length == 3)
                {
                    string groupName = pathParts[2];
                    lblSidebarTitle.Text = $"{groupName}: {dbName}";
                    ShowDatabaseGroupList(db, dbName, groupName, connInfo);
                    ShowDatabaseInfo(db, dbName);
                    if (groupName == "Tables") showTools("點到Tables");
                    else if (groupName == "Views") showTools("點到Views本身");
                    else if (groupName == "Functions") showTools("點到Functions本身");
                }
                
                // 如果是 Table 節點，則側邊欄顯示 Table 詳情
                if (pathParts.Length >= 4 && pathParts[2] == "Tables")
                {
                    string tableName = pathParts[3];
                    lblSidebarTitle.Text = $"Table: {tableName}";
                    // 只有當 table_top 還沒載入過或是不同 DB 時才重載列表
                    ShowDatabaseObjectList(db, dbName); 
                    ShowTableDetails(db, dbName, tableName);
                    showTools("點到Table本身");
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Views")
                {
                    string viewName = pathParts[3];
                    lblSidebarTitle.Text = $"View: {viewName}";
                    ShowDatabaseGroupList(db, dbName, "Views", connInfo);
                    ShowViewDetails(db, dbName, viewName);
                    showTools("點到View本身");
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Events")
                {
                    string eventName = pathParts[3];
                    lblSidebarTitle.Text = $"Event: {eventName}";
                    ShowDatabaseGroupList(db, dbName, "Events", connInfo);
                    ShowEventDetails(db, dbName, eventName);
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Functions")
                {
                    string functionName = pathParts[3];
                    lblSidebarTitle.Text = $"Function: {functionName}";
                    ShowDatabaseGroupList(db, dbName, "Functions", connInfo);
                    ShowFunctionDetails(db, dbName, functionName);
                    showTools("點到Function本身");
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Reports")
                {
                    string reportName = pathParts[3];
                    lblSidebarTitle.Text = $"Report: {reportName}";
                    ShowDatabaseReport(db, dbName, reportName, connInfo);
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
                string objectName = table_top.Rows[e.RowIndex].Cells["名稱"].Value.ToString();
                string groupName = GetCurrentGridGroupName(e.RowIndex);
                SyncTreeWithDatabaseObject(objectName, groupName);

                var cms = new ContextMenuStrip();
                if (groupName == "Views")
                {
                    var itemOpen = new ToolStripMenuItem(Localization.T("Tool.OpenView"));
                    itemOpen.Click += (s, ev) => OpenSelectedViewInQuery();
                    cms.Items.Add(itemOpen);

                    var itemDesign = new ToolStripMenuItem(Localization.T("Tool.DesignView"));
                    itemDesign.Click += (s, ev) => ShowSelectedViewDefinition();
                    cms.Items.Add(itemDesign);

                    var itemDump = new ToolStripMenuItem(Localization.T("Tool.DumpSql"));
                    itemDump.Click += (s, ev) => DumpSelectedViewSql();
                    cms.Items.Add(itemDump);

                    var itemDrop = new ToolStripMenuItem(Localization.T("Tool.DeleteView"));
                    itemDrop.Click += (s, ev) => DeleteSelectedView();
                    cms.Items.Add(itemDrop);
                }
                else if (groupName == "Tables")
                {
                    var itemOpen = new ToolStripMenuItem(Localization.T("Tool.OpenTable"));
                    itemOpen.Click += (s, ev) => OpenSelectedTableInQuery();
                    cms.Items.Add(itemOpen);

                    var itemDesign = new ToolStripMenuItem(Localization.T("Tool.DesignTable"));
                    itemDesign.Click += (s, ev) => DesignSelectedTable();
                    cms.Items.Add(itemDesign);

                    var itemDump = new ToolStripMenuItem(Localization.T("Tool.DumpSql"));
                    itemDump.Click += (s, ev) => DumpSelectedTableSql(false);
                    cms.Items.Add(itemDump);

                    var itemDrop = new ToolStripMenuItem(Localization.T("Tool.DeleteTable"));
                    itemDrop.Click += (s, ev) => DeleteSelectedTable();
                    cms.Items.Add(itemDrop);
                }
                else if (groupName == "Backups")
                {
                    var itemBackup = new ToolStripMenuItem(Localization.T("Tool.CreateBackup"));
                    itemBackup.Click += (s, ev) => BackupSelectedDatabaseWithDialog();
                    cms.Items.Add(itemBackup);
                }
                else if (groupName == "Functions")
                {
                    var itemExecute = new ToolStripMenuItem(Localization.T("Tool.ExecuteFunction"));
                    itemExecute.Click += (s, ev) => ExecuteSelectedFunction();
                    cms.Items.Add(itemExecute);

                    var itemDesign = new ToolStripMenuItem(Localization.T("Tool.DesignFunction"));
                    itemDesign.Click += (s, ev) => ShowSelectedFunctionDefinition();
                    cms.Items.Add(itemDesign);

                    var itemDrop = new ToolStripMenuItem(Localization.T("Tool.DeleteFunction"));
                    itemDrop.Click += (s, ev) => DeleteSelectedFunction();
                    cms.Items.Add(itemDrop);
                }
                else if (groupName == "Queries")
                {
                    var itemOpen = new ToolStripMenuItem(Localization.T("Tool.OpenQuery"));
                    itemOpen.Click += (s, ev) => OpenSelectedQueryTabFromGrid();
                    cms.Items.Add(itemOpen);

                    var itemClose = new ToolStripMenuItem(Localization.T("Tool.CloseQuery"));
                    itemClose.Click += (s, ev) => CloseSelectedQueryTabFromGrid();
                    cms.Items.Add(itemClose);
                }

                if (cms.Items.Count == 0)
                {
                    cms.Dispose();
                    return;
                }
                
                ThemeManager.ApplyToolStrip(cms);
                cms.Show(table_top, table_top.PointToClient(Cursor.Position));
            }
        }

        private void table_top_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            string groupName = GetCurrentGridGroupName(e.RowIndex);
            if (groupName == "Queries")
            {
                ActivateQueryTabFromGridRow(e.RowIndex);
            }
        }

        private bool OpenSelectedQueryTabFromGrid()
        {
            if (table_top.SelectedRows.Count == 0) return false;
            return ActivateQueryTabFromGridRow(table_top.SelectedRows[0].Index);
        }

        private bool CloseSelectedQueryTabFromGrid()
        {
            if (table_top.SelectedRows.Count == 0) return false;
            int tabIndex = GetQueryTabIndexFromGridRow(table_top.SelectedRows[0].Index);
            if (tabIndex < 0 || tabIndex >= queryTabs.TabPages.Count) return false;

            TabPage page = queryTabs.TabPages[tabIndex];
            if (page.Tag is QueryForm queryForm)
            {
                queryForm.Close();
            }
            else
            {
                queryTabs.TabPages.Remove(page);
                page.Dispose();
            }

            RefreshQueriesGroupIfSelected();
            return true;
        }

        private bool ActivateQueryTabFromGridRow(int rowIndex)
        {
            int tabIndex = GetQueryTabIndexFromGridRow(rowIndex);
            if (tabIndex < 0 || tabIndex >= queryTabs.TabPages.Count) return false;

            table_top.Visible = false;
            queryTabs.Visible = true;
            queryTabs.SelectedIndex = tabIndex;
            queryTabs.BringToFront();
            UpdateMainStatus("Query tab opened: " + queryTabs.TabPages[tabIndex].Text);
            return true;
        }

        private int GetQueryTabIndexFromGridRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= table_top.Rows.Count || !table_top.Columns.Contains("分頁索引")) return -1;
            object value = table_top.Rows[rowIndex].Cells["分頁索引"].Value;
            if (value == null || value == DBNull.Value) return -1;
            int tabIndex;
            return int.TryParse(value.ToString(), out tabIndex) ? tabIndex : -1;
        }

        private string GetCurrentGridGroupName(int rowIndex)
        {
            if (table_top.Columns.Contains("類型") && rowIndex >= 0 && rowIndex < table_top.Rows.Count)
            {
                object typeValue = table_top.Rows[rowIndex].Cells["類型"].Value;
                if (typeValue != null && string.Equals(typeValue.ToString(), "View", StringComparison.OrdinalIgnoreCase))
                {
                    return "Views";
                }
                if (typeValue != null && string.Equals(typeValue.ToString(), "Query", StringComparison.OrdinalIgnoreCase))
                {
                    return "Queries";
                }
            }

            if (db_tree.SelectedNode != null)
            {
                var pathParts = my.explode("\\", db_tree.SelectedNode.FullPath);
                if (pathParts.Length >= 3 && (pathParts[2] == "Views" || pathParts[2] == "Tables" || pathParts[2] == "Backups" || pathParts[2] == "Functions" || pathParts[2] == "Queries"))
                {
                    return pathParts[2];
                }
            }

            return "Tables";
        }

        private void SyncTreeWithDatabaseObject(string objectName, string groupName)
        {
            if (db_tree.SelectedNode == null) return;

            TreeNode databaseNode = GetSelectedDatabaseNode();
            if (databaseNode == null) return;

            foreach (TreeNode group in databaseNode.Nodes)
            {
                if (!string.Equals(group.Text, groupName, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (TreeNode node in group.Nodes)
                {
                    if (node.Text == objectName)
                    {
                        db_tree.SelectedNode = node;
                        return;
                    }
                }
            }
        }

        private void db_tree_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedDatabaseObjectToInternalClipboard();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.V)
            {
                PasteInternalClipboardToSelectedDatabase();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.F2)
            {
                BeginRenameSelectedDatabaseObject();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

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

        private void BeginRenameSelectedDatabaseObject()
        {
            if (!IsRenameableObjectNode(db_tree.SelectedNode))
            {
                MessageBox.Show("請先選取單一 Table 或 View。", "重新命名", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _allowTreeLabelEdit = true;
            db_tree.SelectedNode.BeginEdit();
        }

        private void db_tree_BeforeLabelEdit(object sender, NodeLabelEditEventArgs e)
        {
            if (!_allowTreeLabelEdit || !IsRenameableObjectNode(e.Node))
            {
                e.CancelEdit = true;
                _allowTreeLabelEdit = false;
            }
        }

        private void db_tree_AfterLabelEdit(object sender, NodeLabelEditEventArgs e)
        {
            _allowTreeLabelEdit = false;
            e.CancelEdit = true;

            if (e.Node == null || e.Label == null) return;
            string newName = e.Label.Trim();
            string oldName = e.Node.Text;
            if (newName.Length == 0 || newName == oldName) return;

            RenameDatabaseObjectNode(e.Node, oldName, newName);
        }

        private bool IsRenameableObjectNode(TreeNode node)
        {
            if (node == null) return false;
            var pathParts = my.explode("\\", node.FullPath);
            return pathParts.Length >= 4 && (pathParts[2] == "Tables" || pathParts[2] == "Views");
        }

        private void RenameDatabaseObjectNode(TreeNode node, string oldName, string newName)
        {
            DatabaseCopyItem item = BuildCopyItemFromNode(node);
            if (item == null)
            {
                MessageBox.Show("無法取得目前選取物件的連線資訊。", "重新命名", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                bool isView = item.ObjectKind == "view";
                if (isView ? item.Database.ViewExists(item.DatabaseName, newName) : item.Database.TableExists(item.DatabaseName, newName))
                {
                    MessageBox.Show("目標名稱已存在：" + newName, "重新命名", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Cursor = Cursors.WaitCursor;
                if (isView)
                    item.Database.RenameView(item.DatabaseName, oldName, newName);
                else
                    item.Database.RenameTable(item.DatabaseName, oldName, newName);

                node.Text = newName;
                db_tree.SelectedNode = node;
                UpdateMainStatus("Renamed " + item.ObjectKind + ": " + oldName + " -> " + newName);
                db_tree_AfterSelect(db_tree, new TreeViewEventArgs(node));
            }
            catch (Exception ex)
            {
                UpdateMainStatus("Rename failed: " + ex.Message);
                MessageBox.Show("重新命名失敗：" + ex.Message, "重新命名", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void CopySelectedDatabaseObjectToInternalClipboard()
        {
            DatabaseCopyItem item = BuildCopyItemFromNode(db_tree.SelectedNode);
            if (item == null)
            {
                MessageBox.Show("請先選取單一 Table 或 View。", "複製物件", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _treeClipboardItem = item;
            UpdateMainStatus("Copied " + item.ObjectKind + ": " + item.DatabaseName + "." + item.ObjectName);
        }

        private async void PasteInternalClipboardToSelectedDatabase()
        {
            if (_treeClipboardItem == null)
            {
                MessageBox.Show("尚未複製任何 Table 或 View。", "貼上物件", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
            if (target == null)
            {
                MessageBox.Show("請先選取目標 database 或其 Tables/Views 節點。", "貼上物件", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DatabaseCopyItem targetItem = new DatabaseCopyItem
            {
                Database = target.Database,
                DatabaseName = target.DatabaseName,
                ProviderName = target.ProviderName,
                ObjectKind = _treeClipboardItem.ObjectKind
            };

            try
            {
                Cursor = Cursors.WaitCursor;
                var service = new DatabaseCopyService(1000);
                DatabaseCopyResult result = await Task.Run(() => service.Copy(_treeClipboardItem, targetItem, p =>
                {
                    if (!string.IsNullOrEmpty(p.Message)) UpdateMainStatus(p.Message);
                }));

                RefreshDatabaseObjectNodes(target.DatabaseNode);
                SelectObjectNode(target.DatabaseNode, result.ObjectKind, result.TargetName);
                UpdateMainStatus("Copy completed: " + result.TargetName + (result.ObjectKind == "table" ? " (" + result.CopiedRows + " rows)" : ""));
            }
            catch (Exception ex)
            {
                UpdateMainStatus("Copy failed: " + ex.Message);
                MessageBox.Show("複製失敗：" + ex.Message, "貼上物件", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private DatabaseCopyItem BuildCopyItemFromNode(TreeNode node)
        {
            if (node == null) return null;
            var pathParts = my.explode("\\", node.FullPath);
            if (pathParts.Length < 4) return null;
            if (pathParts[2] != "Tables" && pathParts[2] != "Views") return null;

            TreeNode root = node;
            while (root.Parent != null) root = root.Parent;
            var connInfo = myN.connections[root.Index];
            if (connInfo["isConnect"].ToString() != "T" || !(connInfo["pdo"] is IDatabase db)) return null;

            return new DatabaseCopyItem
            {
                Database = db,
                DatabaseName = pathParts[1],
                ObjectName = pathParts[3],
                ObjectKind = pathParts[2] == "Views" ? "view" : "table",
                ProviderName = db.ProviderName
            };
        }

        private TreeDatabaseTarget BuildTargetFromNode(TreeNode node)
        {
            if (node == null) return null;
            var pathParts = my.explode("\\", node.FullPath);
            if (pathParts.Length < 2) return null;

            TreeNode root = node;
            while (root.Parent != null) root = root.Parent;
            var connInfo = myN.connections[root.Index];
            if (connInfo["isConnect"].ToString() != "T" || !(connInfo["pdo"] is IDatabase db)) return null;

            TreeNode dbNode = node;
            while (dbNode.Parent != null && dbNode.Parent.Parent != null) dbNode = dbNode.Parent;
            if (dbNode.Parent == null) return null;

            return new TreeDatabaseTarget
            {
                Database = db,
                DatabaseName = pathParts[1],
                ProviderName = db.ProviderName,
                DatabaseNode = dbNode,
                ConnectionInfo = connInfo
            };
        }

        private void RefreshDatabaseObjectNodes(TreeNode databaseNode)
        {
            if (databaseNode == null) return;
            TreeNode root = databaseNode;
            while (root.Parent != null) root = root.Parent;
            IDatabase db = (IDatabase)myN.connections[root.Index]["pdo"];
            databaseNode.Nodes.Clear();
            PopulateDatabaseChildren(databaseNode, db, databaseNode.Text);
            databaseNode.Expand();
        }

        private void SelectObjectNode(TreeNode databaseNode, string objectKind, string objectName)
        {
            if (databaseNode == null) return;
            string groupName = objectKind == "view" ? "Views" : "Tables";
            foreach (TreeNode group in databaseNode.Nodes)
            {
                if (group.Text != groupName) continue;
                foreach (TreeNode item in group.Nodes)
                {
                    if (item.Text == objectName)
                    {
                        db_tree.SelectedNode = item;
                        item.EnsureVisible();
                        return;
                    }
                }
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
                lblSidebarTitle.Text = Localization.T("Sidebar.ObjectDetails");
                dgvDetails.DataSource = null;
                rtbDDL.Clear();

                Control[] navControls = this.Controls.Find("lblNav", true);
                if (navControls.Length > 0) ((Label)navControls[0]).Text = " " + Localization.T("Status.Ready");
                
                MessageBox.Show(Localization.T("Status.ConnectionClosed"));
            }
        }

        private TreeNode GetSelectedConnectionRoot()
        {
            TreeNode node = db_tree.SelectedNode;
            if (node == null) return null;

            while (node.Parent != null)
            {
                node = node.Parent;
            }

            return node.Index >= 0 && node.Index < myN.connections.Count ? node : null;
        }

        private void CloseSelectedTab()
        {
            if (queryTabs == null || queryTabs.SelectedTab == null)
            {
                UpdateMainStatus(Localization.T("Status.SelectTab"));
                return;
            }

            TabPage page = queryTabs.SelectedTab;
            TableDesignerForm designer = page.Controls.OfType<TableDesignerForm>().FirstOrDefault();
            if (designer != null && !designer.ConfirmClose())
            {
                return;
            }

            Form form = page.Tag as Form;
            if (form != null)
            {
                form.Close();
            }

            queryTabs.TabPages.Remove(page);
            if (queryTabs.TabPages.Count == 0) queryTabs.Visible = false;
            UpdateMainStatus(Localization.T("Status.TabClosed"));
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

        private void ShowDatabaseGroupList(IDatabase db, string dbName, string groupName, Dictionary<string, object> connInfo = null)
        {
            if (string.Equals(groupName, "Tables", StringComparison.OrdinalIgnoreCase))
            {
                ShowDatabaseObjectList(db, dbName);
                return;
            }

            DataTable displayDt = new DataTable();
            displayDt.Columns.Add("名稱");
            displayDt.Columns.Add("類型");
            displayDt.Columns.Add("狀態");

            if (string.Equals(groupName, "Views", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string viewName in db.GetViews(dbName))
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = viewName;
                    row["類型"] = "View";
                    row["狀態"] = "Ready";
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "Backups", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("路徑");
                DataRow row = displayDt.NewRow();
                row["名稱"] = dbName;
                row["類型"] = db is my_sqlite ? "SQLite File" : "SQL Dump";
                string sqlitePath = GetSQLiteDatabasePath(connInfo, db);
                row["狀態"] = db is my_sqlite && !File.Exists(sqlitePath) ? "Missing source" : "Ready";
                row["路徑"] = string.IsNullOrWhiteSpace(sqlitePath) ? "(logical backup)" : sqlitePath;
                displayDt.Rows.Add(row);
            }
            else if (string.Equals(groupName, "Events", StringComparison.OrdinalIgnoreCase))
            {
                foreach (DataRow eventRow in GetDatabaseEvents(db, dbName).Rows)
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = eventRow["Name"];
                    row["類型"] = eventRow["Type"];
                    row["狀態"] = eventRow["Status"];
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "Functions", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("回傳型別");
                foreach (DataRow functionRow in GetDatabaseFunctions(db, dbName).Rows)
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = functionRow["Name"];
                    row["類型"] = functionRow["Type"];
                    row["狀態"] = functionRow["Status"];
                    row["回傳型別"] = functionRow["ReturnType"];
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "Queries", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("資料庫");
                displayDt.Columns.Add("編號");
                displayDt.Columns.Add("分頁索引");
                displayDt.Columns.Add("SQL");
                foreach (OpenQueryTabInfo queryInfo in GetOpenQueryTabs(dbName))
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = queryInfo.Page.Text;
                    row["類型"] = "Query";
                    row["狀態"] = string.IsNullOrWhiteSpace(queryInfo.Query.CurrentStatus) ? "Ready" : queryInfo.Query.CurrentStatus;
                    row["資料庫"] = queryInfo.Query.DatabaseName;
                    row["編號"] = queryInfo.DisplayIndex;
                    row["分頁索引"] = queryInfo.TabIndex;
                    row["SQL"] = BuildQueryPreview(queryInfo.Query.CurrentSql);
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "Reports", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("描述");
                foreach (string reportName in DatabaseReportNames)
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = reportName;
                    row["類型"] = "Report";
                    row["狀態"] = "Ready";
                    row["描述"] = GetDatabaseReportDescription(reportName);
                    displayDt.Rows.Add(row);
                }
            }
            else
            {
                DataRow row = displayDt.NewRow();
                row["名稱"] = groupName;
                row["類型"] = groupName;
                row["狀態"] = "Empty";
                displayDt.Rows.Add(row);
            }

            table_top.DataSource = displayDt;
            if (table_top.Columns.Contains("分頁索引"))
            {
                table_top.Columns["分頁索引"].Visible = false;
            }
        }

        private List<OpenQueryTabInfo> GetOpenQueryTabs(string databaseName)
        {
            List<OpenQueryTabInfo> tabs = new List<OpenQueryTabInfo>();
            if (queryTabs == null) return tabs;

            for (int i = 0; i < queryTabs.TabPages.Count; i++)
            {
                TabPage page = queryTabs.TabPages[i];
                QueryForm query = page.Tag as QueryForm;
                if (query == null) continue;
                if (!string.Equals(query.DatabaseName, databaseName ?? string.Empty, StringComparison.OrdinalIgnoreCase)) continue;

                tabs.Add(new OpenQueryTabInfo
                {
                    DisplayIndex = tabs.Count + 1,
                    TabIndex = i,
                    Page = page,
                    Query = query
                });
            }

            return tabs;
        }

        private static string BuildQueryPreview(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return string.Empty;
            string preview = sql.Replace("\r", " ").Replace("\n", " ").Trim();
            while (preview.Contains("  ")) preview = preview.Replace("  ", " ");
            return preview.Length <= 120 ? preview : preview.Substring(0, 117) + "...";
        }

        private void RefreshQueriesGroupIfSelected()
        {
            if (db_tree.SelectedNode == null) return;
            var pathParts = my.explode("\\", db_tree.SelectedNode.FullPath);
            if (pathParts.Length < 3 || pathParts[2] != "Queries") return;

            TreeNode root = db_tree.SelectedNode;
            while (root.Parent != null) root = root.Parent;
            if (root.Index < 0 || root.Index >= myN.connections.Count) return;
            var connInfo = myN.connections[root.Index];
            if (connInfo["isConnect"].ToString() != "T" || !(connInfo["pdo"] is IDatabase db)) return;

            ShowDatabaseGroupList(db, pathParts[1], "Queries", connInfo);
        }

        private void ShowDatabaseReport(IDatabase db, string dbName, string reportName, Dictionary<string, object> connInfo = null)
        {
            table_top.DataSource = BuildDatabaseReport(db, dbName, reportName, connInfo);
            ShowReportDetails(db, dbName, reportName);
        }

        private DataTable BuildDatabaseReport(IDatabase db, string dbName, string reportName, Dictionary<string, object> connInfo = null)
        {
            if (string.Equals(reportName, "Table Row Counts", StringComparison.OrdinalIgnoreCase))
            {
                return BuildTableRowCountReport(db, dbName);
            }

            if (string.Equals(reportName, "Object Inventory", StringComparison.OrdinalIgnoreCase))
            {
                return BuildObjectInventoryReport(db, dbName, connInfo);
            }

            return BuildDatabaseSummaryReport(db, dbName, connInfo);
        }

        private DataTable BuildDatabaseSummaryReport(IDatabase db, string dbName, Dictionary<string, object> connInfo = null)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("項目");
            dt.Columns.Add("數值");

            List<string> tables = GetTablesSafe(db, dbName);
            List<string> views = GetViewsSafe(db, dbName);
            DataTable functions = GetDatabaseFunctions(db, dbName);
            DataTable events = GetDatabaseEvents(db, dbName);

            AddReportMetric(dt, "資料庫", dbName);
            AddReportMetric(dt, "Provider", db.ProviderName);
            AddReportMetric(dt, "資料表數", tables.Count.ToString());
            AddReportMetric(dt, "檢視數", views.Count.ToString());
            AddReportMetric(dt, "函式/程序數", functions.Rows.Count.ToString());
            AddReportMetric(dt, "事件/Trigger 數", events.Rows.Count.ToString());
            AddReportMetric(dt, "開啟中查詢分頁", GetOpenQueryTabs(dbName).Count.ToString());
            AddReportMetric(dt, "備份來源", GetBackupSourceDescription(db, connInfo));
            return dt;
        }

        private DataTable BuildTableRowCountReport(IDatabase db, string dbName)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("資料表");
            dt.Columns.Add("列數");
            dt.Columns.Add("狀態");

            foreach (string tableName in GetTablesSafe(db, dbName))
            {
                DataRow row = dt.NewRow();
                row["資料表"] = tableName;
                try
                {
                    row["列數"] = db.CountRows(dbName, tableName).ToString();
                    row["狀態"] = "Ready";
                }
                catch (Exception ex)
                {
                    row["列數"] = "";
                    row["狀態"] = ex.Message;
                }
                dt.Rows.Add(row);
            }

            return dt;
        }

        private DataTable BuildObjectInventoryReport(IDatabase db, string dbName, Dictionary<string, object> connInfo = null)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("名稱");
            dt.Columns.Add("類型");
            dt.Columns.Add("狀態");

            foreach (string tableName in GetTablesSafe(db, dbName))
            {
                AddInventoryRow(dt, tableName, "Table", "Ready");
            }

            foreach (string viewName in GetViewsSafe(db, dbName))
            {
                AddInventoryRow(dt, viewName, "View", "Ready");
            }

            foreach (DataRow row in GetDatabaseFunctions(db, dbName).Rows)
            {
                AddInventoryRow(dt, row["Name"].ToString(), row["Type"].ToString(), row["Status"].ToString());
            }

            foreach (DataRow row in GetDatabaseEvents(db, dbName).Rows)
            {
                AddInventoryRow(dt, row["Name"].ToString(), row["Type"].ToString(), row["Status"].ToString());
            }

            AddInventoryRow(dt, dbName, db is my_sqlite ? "SQLite Backup Source" : "SQL Backup Target", GetBackupSourceDescription(db, connInfo));
            return dt;
        }

        private static void AddReportMetric(DataTable dt, string name, string value)
        {
            DataRow row = dt.NewRow();
            row["項目"] = name;
            row["數值"] = value ?? string.Empty;
            dt.Rows.Add(row);
        }

        private static void AddInventoryRow(DataTable dt, string name, string type, string status)
        {
            DataRow row = dt.NewRow();
            row["名稱"] = name ?? string.Empty;
            row["類型"] = type ?? string.Empty;
            row["狀態"] = status ?? string.Empty;
            dt.Rows.Add(row);
        }

        private static string GetDatabaseReportDescription(string reportName)
        {
            if (string.Equals(reportName, "Table Row Counts", StringComparison.OrdinalIgnoreCase))
            {
                return "列出所有資料表目前列數";
            }

            if (string.Equals(reportName, "Object Inventory", StringComparison.OrdinalIgnoreCase))
            {
                return "彙整資料表、檢視、函式、事件與備份目標";
            }

            return "彙整資料庫物件數量與目前開啟狀態";
        }

        private static List<string> GetTablesSafe(IDatabase db, string dbName)
        {
            try { return db.GetTables(dbName) ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        private static List<string> GetViewsSafe(IDatabase db, string dbName)
        {
            try { return db.GetViews(dbName) ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        private static string GetBackupSourceDescription(IDatabase db, Dictionary<string, object> connInfo)
        {
            if (!(db is my_sqlite)) return "Logical SQL dump";
            string sqlitePath = GetSQLiteDatabasePath(connInfo, db);
            if (string.IsNullOrWhiteSpace(sqlitePath)) return "(unknown SQLite file)";
            return File.Exists(sqlitePath) ? sqlitePath : "Missing source: " + sqlitePath;
        }

        private DataTable GetDatabaseEvents(IDatabase db, string dbName)
        {
            DataTable events = CreateDatabaseEventTable();

            if (db is my_sqlite)
            {
                AppendDatabaseEventsFromQuery(events, db,
                    "SELECT name AS Name, 'Trigger' AS Type, tbl_name AS Status, COALESCE(sql, '') AS DDL " +
                    "FROM sqlite_master WHERE type='trigger' ORDER BY name;");
                return events;
            }

            if (db is my_mysql)
            {
                string safeDb = EscapeSqlLiteral(dbName);
                AppendDatabaseEventsFromQuery(events, db,
                    "SELECT EVENT_NAME AS Name, 'Event' AS Type, STATUS AS Status, EVENT_DEFINITION AS DDL " +
                    "FROM information_schema.EVENTS WHERE EVENT_SCHEMA='" + safeDb + "' ORDER BY EVENT_NAME;");
                AppendDatabaseEventsFromQuery(events, db,
                    "SELECT TRIGGER_NAME AS Name, 'Trigger' AS Type, EVENT_MANIPULATION AS Status, ACTION_STATEMENT AS DDL " +
                    "FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA='" + safeDb + "' ORDER BY TRIGGER_NAME;");
                return events;
            }

            if (db is my_postgresql)
            {
                AppendDatabaseEventsFromQuery(events, db,
                    "SELECT trigger_name AS \"Name\", 'Trigger' AS \"Type\", event_manipulation AS \"Status\", action_statement AS \"DDL\" " +
                    "FROM information_schema.triggers WHERE trigger_schema = 'public' ORDER BY trigger_name;");
                return events;
            }

            if (db is my_mssql)
            {
                AppendDatabaseEventsFromQuery(events, db,
                    "SELECT t.name AS [Name], 'Trigger' AS [Type], CASE WHEN t.is_disabled = 1 THEN 'DISABLED' ELSE 'ENABLED' END AS [Status], " +
                    "COALESCE(OBJECT_DEFINITION(t.object_id), '') AS [DDL] FROM [" + EscapeSqlServerName(dbName) + "].sys.triggers t ORDER BY t.name;");
                return events;
            }

            if (db is my_oracle)
            {
                string owner = EscapeSqlLiteral(dbName.ToUpperInvariant());
                AppendDatabaseEventsFromQuery(events, db,
                    "SELECT TRIGGER_NAME AS Name, 'Trigger' AS Type, STATUS AS Status, TRIGGER_BODY AS DDL " +
                    "FROM ALL_TRIGGERS WHERE OWNER='" + owner + "' ORDER BY TRIGGER_NAME");
            }

            return events;
        }

        private DataTable GetDatabaseFunctions(IDatabase db, string dbName)
        {
            DataTable functions = CreateDatabaseFunctionTable();

            if (db is my_sqlite)
            {
                return functions;
            }

            if (db is my_mysql)
            {
                string safeDb = EscapeSqlLiteral(dbName);
                AppendDatabaseFunctionsFromQuery(functions, db,
                    "SELECT ROUTINE_NAME AS Name, ROUTINE_TYPE AS Type, COALESCE(DATA_TYPE, '') AS ReturnType, IS_DETERMINISTIC AS Status, COALESCE(ROUTINE_DEFINITION, '') AS DDL " +
                    "FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA='" + safeDb + "' ORDER BY ROUTINE_TYPE, ROUTINE_NAME;");
                return functions;
            }

            if (db is my_postgresql)
            {
                AppendDatabaseFunctionsFromQuery(functions, db,
                    "SELECT p.proname AS \"Name\", CASE WHEN p.prokind = 'p' THEN 'Procedure' ELSE 'Function' END AS \"Type\", " +
                    "COALESCE(pg_catalog.pg_get_function_result(p.oid), '') AS \"ReturnType\", COALESCE(l.lanname, '') AS \"Status\", pg_catalog.pg_get_functiondef(p.oid) AS \"DDL\" " +
                    "FROM pg_catalog.pg_proc p INNER JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace " +
                    "LEFT JOIN pg_catalog.pg_language l ON l.oid = p.prolang WHERE n.nspname = 'public' ORDER BY p.proname;");
                return functions;
            }

            if (db is my_mssql)
            {
                AppendDatabaseFunctionsFromQuery(functions, db,
                    "SELECT o.name AS [Name], CASE WHEN o.type = 'P' THEN 'Procedure' ELSE 'Function' END AS [Type], '' AS [ReturnType], o.type_desc AS [Status], " +
                    "COALESCE(OBJECT_DEFINITION(o.object_id), '') AS [DDL] FROM [" + EscapeSqlServerName(dbName) + "].sys.objects o " +
                    "WHERE o.type IN ('FN','IF','TF','FS','FT','P') ORDER BY o.type_desc, o.name;");
                return functions;
            }

            if (db is my_oracle)
            {
                string owner = EscapeSqlLiteral(dbName.ToUpperInvariant());
                AppendDatabaseFunctionsFromQuery(functions, db,
                    "SELECT OBJECT_NAME AS Name, OBJECT_TYPE AS Type, '' AS ReturnType, STATUS AS Status, '' AS DDL " +
                    "FROM ALL_OBJECTS WHERE OWNER='" + owner + "' AND OBJECT_TYPE IN ('FUNCTION','PROCEDURE') ORDER BY OBJECT_TYPE, OBJECT_NAME");
            }

            return functions;
        }

        private static DataTable CreateDatabaseEventTable()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("Name");
            dt.Columns.Add("Type");
            dt.Columns.Add("Status");
            dt.Columns.Add("DDL");
            return dt;
        }

        private static DataTable CreateDatabaseFunctionTable()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("Name");
            dt.Columns.Add("Type");
            dt.Columns.Add("ReturnType");
            dt.Columns.Add("Status");
            dt.Columns.Add("DDL");
            return dt;
        }

        private static void AppendDatabaseEventsFromQuery(DataTable target, IDatabase db, string sql)
        {
            try
            {
                AppendDatabaseEvents(target, db.SelectSQL(sql));
            }
            catch
            {
                // Metadata visibility differs by provider and permissions. Keep the group usable when one source is unavailable.
            }
        }

        private static void AppendDatabaseEvents(DataTable target, DataTable source)
        {
            if (source == null) return;
            foreach (DataRow sourceRow in source.Rows)
            {
                DataRow row = target.NewRow();
                row["Name"] = GetColumnValue(sourceRow, "Name");
                row["Type"] = GetColumnValue(sourceRow, "Type");
                row["Status"] = GetColumnValue(sourceRow, "Status");
                row["DDL"] = GetColumnValue(sourceRow, "DDL");
                if (row["Name"].ToString().Length > 0) target.Rows.Add(row);
            }
        }

        private static void AppendDatabaseFunctionsFromQuery(DataTable target, IDatabase db, string sql)
        {
            try
            {
                AppendDatabaseFunctions(target, db.SelectSQL(sql));
            }
            catch
            {
                // Routine metadata can be hidden by provider permissions. Keep the Functions group usable.
            }
        }

        private static void AppendDatabaseFunctions(DataTable target, DataTable source)
        {
            if (source == null) return;
            foreach (DataRow sourceRow in source.Rows)
            {
                DataRow row = target.NewRow();
                row["Name"] = GetColumnValue(sourceRow, "Name");
                row["Type"] = GetColumnValue(sourceRow, "Type");
                row["ReturnType"] = GetColumnValue(sourceRow, "ReturnType");
                row["Status"] = GetColumnValue(sourceRow, "Status");
                row["DDL"] = GetColumnValue(sourceRow, "DDL");
                if (row["Name"].ToString().Length > 0) target.Rows.Add(row);
            }
        }

        private static string GetColumnValue(DataRow row, string name)
        {
            if (row.Table.Columns.Contains(name) && row[name] != DBNull.Value) return row[name].ToString();
            foreach (DataColumn column in row.Table.Columns)
            {
                if (string.Equals(column.ColumnName, name, StringComparison.OrdinalIgnoreCase) && row[column] != DBNull.Value)
                {
                    return row[column].ToString();
                }
            }
            return string.Empty;
        }

        private static string EscapeSqlLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static string EscapeSqlServerName(string name)
        {
            return (name ?? string.Empty).Replace("]", "]]");
        }

        private static string GetSQLiteDatabasePath(Dictionary<string, object> connInfo, IDatabase db)
        {
            if (!(db is my_sqlite)) return string.Empty;

            if (connInfo != null && connInfo.ContainsKey("path") && connInfo["path"] != null)
            {
                string path = connInfo["path"].ToString();
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }

            my_sqlite sqlite = db as my_sqlite;
            if (sqlite != null && sqlite.MCT != null)
            {
                try
                {
                    System.Data.SQLite.SQLiteConnectionStringBuilder builder =
                        new System.Data.SQLite.SQLiteConnectionStringBuilder(sqlite.MCT.ConnectionString);
                    return builder.DataSource;
                }
                catch { }
            }

            return string.Empty;
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

        private void ShowViewDetails(IDatabase db, string dbName, string viewName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            try
            {
                dgvDetails.Rows.Add("類型", "View");
                dgvDetails.Rows.Add("名稱", viewName);
                rtbDDL.Text = db.GetViewCreateStatement(dbName, viewName);
            }
            catch (Exception ex)
            {
                rtbDDL.Text = "Error loading view details: " + ex.Message;
            }
        }

        private void ShowEventDetails(IDatabase db, string dbName, string eventName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            try
            {
                DataRow match = GetDatabaseEvents(db, dbName).Rows
                    .Cast<DataRow>()
                    .FirstOrDefault(row => string.Equals(row["Name"].ToString(), eventName, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    rtbDDL.Text = "Event not found: " + eventName;
                    return;
                }

                dgvDetails.Rows.Add("類型", match["Type"]);
                dgvDetails.Rows.Add("名稱", match["Name"]);
                dgvDetails.Rows.Add("狀態", match["Status"]);
                rtbDDL.Text = match["DDL"].ToString();
            }
            catch (Exception ex)
            {
                rtbDDL.Text = "Error loading event details: " + ex.Message;
            }
        }

        private void ShowFunctionDetails(IDatabase db, string dbName, string functionName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            try
            {
                DataRow match = GetDatabaseFunctions(db, dbName).Rows
                    .Cast<DataRow>()
                    .FirstOrDefault(row => string.Equals(row["Name"].ToString(), functionName, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    rtbDDL.Text = "Function not found: " + functionName;
                    return;
                }

                dgvDetails.Rows.Add("類型", match["Type"]);
                dgvDetails.Rows.Add("名稱", match["Name"]);
                dgvDetails.Rows.Add("回傳型別", match["ReturnType"]);
                dgvDetails.Rows.Add("狀態", match["Status"]);
                rtbDDL.Text = match["DDL"].ToString();
            }
            catch (Exception ex)
            {
                rtbDDL.Text = "Error loading function details: " + ex.Message;
            }
        }

        private void ShowReportDetails(IDatabase db, string dbName, string reportName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            dgvDetails.Rows.Add("類型", "Report");
            dgvDetails.Rows.Add("名稱", reportName);
            dgvDetails.Rows.Add("資料庫", dbName);
            dgvDetails.Rows.Add("Provider", db.ProviderName);
            rtbDDL.Text = "-- Report: " + reportName + Environment.NewLine +
                          "-- " + GetDatabaseReportDescription(reportName);
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
                    ToolStripMenuItem editItem = new ToolStripMenuItem(Localization.T("Tool.EditConnection"));
                    editItem.Click += (s, ev) => db_tree_edit_connection(e.Node.Index);
                    menu.Items.Add(editItem);

                    ToolStripMenuItem deleteItem = new ToolStripMenuItem(Localization.T("Tool.DeleteConnection"));
                    deleteItem.Click += (s, ev) => db_tree_delete_connection(e.Node.Index);
                    menu.Items.Add(deleteItem);
                }
                else
                {
                    var pathParts = my.explode("\\", e.Node.FullPath);
                    if (pathParts.Length >= 4 && pathParts[2] == "Tables")
                    {
                        ToolStripMenuItem openTableItem = new ToolStripMenuItem(Localization.T("Tool.OpenTable"));
                        openTableItem.Click += (s, ev) => OpenSelectedTableInQuery();
                        menu.Items.Add(openTableItem);

                        ToolStripMenuItem designTableItem = new ToolStripMenuItem(Localization.T("Tool.DesignTable"));
                        designTableItem.Click += (s, ev) => DesignSelectedTable();
                        menu.Items.Add(designTableItem);

                        ToolStripMenuItem dumpSqlItem = new ToolStripMenuItem(Localization.T("Tool.DumpSql"));
                        ToolStripMenuItem dumpStructureAndDataItem = new ToolStripMenuItem(Localization.T("Tool.StructureAndData"));
                        dumpStructureAndDataItem.Click += (s, ev) => DumpSelectedTableSql(false);
                        dumpSqlItem.DropDownItems.Add(dumpStructureAndDataItem);

                        ToolStripMenuItem dumpDataOnlyItem = new ToolStripMenuItem(Localization.T("Tool.DataOnly"));
                        dumpDataOnlyItem.Click += (s, ev) => DumpSelectedTableSql(true);
                        dumpSqlItem.DropDownItems.Add(dumpDataOnlyItem);

                        menu.Items.Add(dumpSqlItem);
                    }
                    else if (pathParts.Length >= 4 && pathParts[2] == "Views")
                    {
                        ToolStripMenuItem openViewItem = new ToolStripMenuItem(Localization.T("Tool.OpenView"));
                        openViewItem.Click += (s, ev) => OpenSelectedViewInQuery();
                        menu.Items.Add(openViewItem);

                        ToolStripMenuItem designViewItem = new ToolStripMenuItem(Localization.T("Tool.DesignView"));
                        designViewItem.Click += (s, ev) => ShowSelectedViewDefinition();
                        menu.Items.Add(designViewItem);

                        ToolStripMenuItem dumpSqlItem = new ToolStripMenuItem(Localization.T("Tool.DumpSql"));
                        dumpSqlItem.Click += (s, ev) => DumpSelectedViewSql();
                        menu.Items.Add(dumpSqlItem);

                        ToolStripMenuItem deleteViewItem = new ToolStripMenuItem(Localization.T("Tool.DeleteView"));
                        deleteViewItem.Click += (s, ev) => DeleteSelectedView();
                        menu.Items.Add(deleteViewItem);
                    }
                    else if (pathParts.Length >= 4 && pathParts[2] == "Functions")
                    {
                        ToolStripMenuItem executeFunctionItem = new ToolStripMenuItem(Localization.T("Tool.ExecuteFunction"));
                        executeFunctionItem.Click += (s, ev) => ExecuteSelectedFunction();
                        menu.Items.Add(executeFunctionItem);

                        ToolStripMenuItem designFunctionItem = new ToolStripMenuItem(Localization.T("Tool.DesignFunction"));
                        designFunctionItem.Click += (s, ev) => ShowSelectedFunctionDefinition();
                        menu.Items.Add(designFunctionItem);

                        ToolStripMenuItem deleteFunctionItem = new ToolStripMenuItem(Localization.T("Tool.DeleteFunction"));
                        deleteFunctionItem.Click += (s, ev) => DeleteSelectedFunction();
                        menu.Items.Add(deleteFunctionItem);
                    }
                }

                if (menu.Items.Count == 0)
                {
                    menu.Dispose();
                    return;
                }

                ThemeManager.ApplyToolStrip(menu);
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
                case "sqlite":
                    {
                        sqlite_add_edit form = new sqlite_add_edit();
                        form.F1 = this;
                        form.editIndex = index;
                        form.ShowDialog();
                    }
                    break;
                case "mssql":
                case "sqlserver":
                    {
                        sqlserver_add_edit form = new sqlserver_add_edit();
                        form.F1 = this;
                        form.editIndex = index;
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
            TreeNode databaseNode = db_tree.Nodes[father_index].Nodes[index];
            if (databaseNode.Nodes.Count > 0) return;
            if (!(myN.connections[father_index]["pdo"] is IDatabase db)) return;

            PopulateDatabaseChildren(databaseNode, db, databaseName);
            databaseNode.Expand();
            databaseNode.ImageIndex = 11;
            databaseNode.SelectedImageIndex = 11;
        }

        private void PopulateDatabaseChildren(TreeNode databaseNode, IDatabase db, string databaseName)
        {
            TreeNode tablesNode = new TreeNode("Tables");
            tablesNode.ImageIndex = 12;
            tablesNode.SelectedImageIndex = 12;
            databaseNode.Nodes.Add(tablesNode);

            foreach (string tableName in db.GetTables(databaseName))
            {
                TreeNode tN = new TreeNode(tableName);
                tN.SelectedImageIndex = 12;
                tN.ImageIndex = 12;
                tablesNode.Nodes.Add(tN);
            }

            TreeNode viewsNode = new TreeNode("Views");
            viewsNode.ImageIndex = 13;
            viewsNode.SelectedImageIndex = 13;
            databaseNode.Nodes.Add(viewsNode);

            foreach (string viewName in db.GetViews(databaseName))
            {
                TreeNode vN = new TreeNode(viewName);
                vN.SelectedImageIndex = 13;
                vN.ImageIndex = 13;
                viewsNode.Nodes.Add(vN);
            }

            TreeNode newNode = new TreeNode("Functions");
            newNode.ImageIndex = 14;
            newNode.SelectedImageIndex = 14;
            databaseNode.Nodes.Add(newNode);

            foreach (DataRow functionRow in GetDatabaseFunctions(db, databaseName).Rows)
            {
                TreeNode functionNode = new TreeNode(functionRow["Name"].ToString());
                functionNode.ImageIndex = 14;
                functionNode.SelectedImageIndex = 14;
                newNode.Nodes.Add(functionNode);
            }

            newNode = new TreeNode("Events");
            newNode.ImageIndex = 15;
            newNode.SelectedImageIndex = 15;
            databaseNode.Nodes.Add(newNode);

            foreach (DataRow eventRow in GetDatabaseEvents(db, databaseName).Rows)
            {
                TreeNode eventNode = new TreeNode(eventRow["Name"].ToString());
                eventNode.ImageIndex = 15;
                eventNode.SelectedImageIndex = 15;
                newNode.Nodes.Add(eventNode);
            }

            newNode = new TreeNode("Queries");
            newNode.ImageIndex = 16;
            newNode.SelectedImageIndex = 16;
            databaseNode.Nodes.Add(newNode);

            newNode = new TreeNode("Reports");
            newNode.ImageIndex = 17;
            newNode.SelectedImageIndex = 17;
            databaseNode.Nodes.Add(newNode);

            foreach (string reportName in DatabaseReportNames)
            {
                TreeNode reportNode = new TreeNode(reportName);
                reportNode.ImageIndex = 17;
                reportNode.SelectedImageIndex = 17;
                newNode.Nodes.Add(reportNode);
            }

            newNode = new TreeNode("Backups");
            newNode.ImageIndex = 18;
            newNode.SelectedImageIndex = 18;
            databaseNode.Nodes.Add(newNode);
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
            dialog.BackColor = ThemeManager.ElevatedColor;
            dialog.ForeColor = ThemeManager.TextColor;

            dialogLabel.Location = new Point(0, 0);
            dialogLabel.AutoSize = false;
            dialogLabel.Size = new Size(250, 80);
            dialogLabel.TextAlign = ContentAlignment.MiddleCenter;
            dialogLabel.Text = message;
            dialogLabel.ForeColor = ThemeManager.TextColor;
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
                if (m[2] == "Backups")
                {
                    BackupSelectedDatabaseWithDialog();
                    dialogMyBoxOff();
                    return;
                }
                db_tree_third_click(index, tree.SelectedNode.Parent.Index, tree.SelectedNode.Parent.Text, tree.SelectedNode.Text);
                dialogMyBoxOff();
                return;
            }
            if (m.Length == 4)
            {
                // 代表是資料表或檢視層級 -> 直接開啟
                if (m[2] == "Views") OpenSelectedViewInQuery();
                else if (m[2] == "Events") db_tree_AfterSelect(db_tree, new TreeViewEventArgs(tree.SelectedNode));
                else if (m[2] == "Functions") ExecuteSelectedFunction();
                else if (m[2] == "Reports") db_tree_AfterSelect(db_tree, new TreeViewEventArgs(tree.SelectedNode));
                else OpenSelectedTableInQuery();
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
                                    if (!((my_sqlite)myN.connections[index]["pdo"]).SpatiaLiteEnabled)
                                    {
                                        MessageBox.Show(
                                            "SQLite 已連線，但 SpatiaLite 載入失敗：\r\n" + ((my_sqlite)myN.connections[index]["pdo"]).SpatiaLiteLoadError,
                                            "SpatiaLite",
                                            MessageBoxButtons.OK,
                                            MessageBoxIcon.Warning);
                                    }
                                    else
                                    {
                                        UpdateMainStatus("SQLite + SpatiaLite ready");
                                    }
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
                    case "oracle":
                        {
                            myN.connections[index]["connString"] = BuildOracleConnectionString(myN.connections[index]);
                            myN.connections[index]["pdo"] = new my_oracle();
                            ((my_oracle)myN.connections[index]["pdo"]).setConn(myN.connections[index]["connString"].ToString());
                            if (((my_oracle)myN.connections[index]["pdo"]).MCT.State != ConnectionState.Open)
                            {
                                try
                                {
                                    ((my_oracle)myN.connections[index]["pdo"]).open();
                                    myN.connections[index]["isConnect"] = "T";
                                    db_tree.Nodes[index].SelectedImageIndex = 5;
                                    db_tree.Nodes[index].ImageIndex = 5;
                                    List<string> schemas = ((my_oracle)myN.connections[index]["pdo"]).GetDatabases();
                                    for (int i = 0, max_i = schemas.Count; i < max_i; i++)
                                    {
                                        TreeNode newNode = new TreeNode(schemas[i], i, i);
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
                                    ((TreeView)sender).SelectedNode.Collapse();
                                }

                            }
                        }
                        break;
                    case "mssql":
                    case "sqlserver":
                        {
                            var builder = new System.Data.SqlClient.SqlConnectionStringBuilder();
                            string host = myN.connections[index].ContainsKey("host") ? myN.connections[index]["host"].ToString() : "";
                            string port = myN.connections[index].ContainsKey("port") ? myN.connections[index]["port"].ToString() : "";
                            builder.DataSource = string.IsNullOrWhiteSpace(port) ? host : host + "," + port;
                            builder.InitialCatalog = myN.connections[index].ContainsKey("initial_database") && !string.IsNullOrWhiteSpace(myN.connections[index]["initial_database"].ToString())
                                ? myN.connections[index]["initial_database"].ToString()
                                : "master";
                            bool trusted = myN.connections[index].ContainsKey("trusted_connection") && myN.connections[index]["trusted_connection"].ToString() == "T";
                            builder.IntegratedSecurity = trusted;
                            builder.TrustServerCertificate = true;
                            if (!trusted)
                            {
                                builder.UserID = myN.connections[index].ContainsKey("username") ? myN.connections[index]["username"].ToString() : "";
                                builder.Password = myN.connections[index].ContainsKey("pwd") ? myN.connections[index]["pwd"].ToString() : "";
                            }
                            myN.connections[index]["connString"] = builder.ConnectionString;
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
                                    db_tree.Nodes[index].SelectedImageIndex = 9;
                                    db_tree.Nodes[index].ImageIndex = 9;
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
            SelectDatabaseGroupNode("Tables");
            showTools("點到Tables");
        }

        private void view_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("view");
            SelectDatabaseGroupNode("Views");
            showTools("點到Views本身");
        }

        private void user_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("user");
            UpdateMainStatus(Localization.T("Status.UserSelected"));
        }

        private void function_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("function");
            SelectDatabaseGroupNode("Functions");
            showTools("點到Functions本身");
        }

        private void other_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("other");
            UpdateMainStatus(Localization.T("Status.OtherSelected"));
        }

        private void query_section_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("query");
            SelectDatabaseGroupNode("Queries");
        }

        private void backup_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("backup");
            SelectDatabaseGroupNode("Backups");
        }

        private void auto_run_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("auto");
            SelectDatabaseGroupNode("Events");
        }

        private void model_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("model");
            UpdateMainStatus(Localization.T("Status.ModelSelected"));
        }

        private void bi_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("bi");
            UpdateMainStatus(Localization.T("Status.BISelected"));
        }

        private void SelectDatabaseGroupNode(string groupName)
        {
            TreeNode databaseNode = GetSelectedDatabaseNode();
            if (databaseNode == null)
            {
                UpdateMainStatus(Localization.T("Status.SelectExpandedDatabase"));
                return;
            }

            foreach (TreeNode child in databaseNode.Nodes)
            {
                if (string.Equals(child.Text, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    db_tree.SelectedNode = child;
                    child.EnsureVisible();
                    return;
                }
            }

            UpdateMainStatus("目前資料庫沒有 " + groupName + " 節點。");
        }

        private TreeNode GetSelectedDatabaseNode()
        {
            TreeNode node = db_tree.SelectedNode;
            if (node == null) return null;
            if (node.Parent == null) return null;

            while (node.Parent != null && node.Parent.Parent != null)
            {
                node = node.Parent;
            }

            return node.Parent == null ? null : node;
        }

        private void tool_Connection_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void NewConnectionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ShowConnectionTypeSelection();
        }

        private void connection_btn_Click(object sender, EventArgs e)
        {
            ShowConnectionTypeSelection();
        }

        private void ShowConnectionTypeSelection()
        {
            using (ConnectionTypeSelectionForm form = new ConnectionTypeSelectionForm())
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    OpenNewConnectionForm(form.SelectedConnectionType);
                }
            }
        }

        private void OpenNewConnectionForm(string connectionType)
        {
            switch (connectionType)
            {
                case ConnectionTypeSelectionForm.MySql:
                    OpenMySqlConnectionForm();
                    break;
                case ConnectionTypeSelectionForm.PostgreSql:
                    OpenPostgreSqlConnectionForm();
                    break;
                case ConnectionTypeSelectionForm.SqlServer:
                    OpenSqlServerConnectionForm();
                    break;
                case ConnectionTypeSelectionForm.Oracle:
                    OpenOracleConnectionForm();
                    break;
                case ConnectionTypeSelectionForm.Sqlite:
                    OpenSqliteConnectionForm();
                    break;
            }
        }

        private void OpenMySqlConnectionForm()
        {
            mysql_add_edit form = new mysql_add_edit();
            form.F1 = this;
            form.ShowDialog(this);
        }

        private void OpenPostgreSqlConnectionForm()
        {
            postgresql_add_edit form = new postgresql_add_edit();
            form.F1 = this;
            form.ShowDialog(this);
        }

        private void OpenOracleConnectionForm()
        {
            oracle_add_edit form = new oracle_add_edit();
            form.F1 = this;
            form.oracle_connection_type.Text = "Basic";
            form.oracle_connection_type_selected_trigger_change();
            form.ShowDialog(this);
        }

        private void OpenSqliteConnectionForm()
        {
            sqlite_add_edit form = new sqlite_add_edit();
            form.F1 = this;
            form.ShowDialog(this);
        }

        private void OpenSqlServerConnectionForm()
        {
            sqlserver_add_edit form = new sqlserver_add_edit();
            form.F1 = this;
            form.ShowDialog(this);
        }

        private static string BuildOracleConnectionString(Dictionary<string, object> conn)
        {
            if (conn.ContainsKey("connString") && !string.IsNullOrWhiteSpace(conn["connString"]?.ToString()))
            {
                return conn["connString"].ToString();
            }

            Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder builder =
                new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder();
            builder.UserID = GetConnectionValue(conn, "username");
            builder.Password = GetConnectionValue(conn, "pwd");
            builder.DataSource = string.Equals(GetConnectionValue(conn, "connection_type"), "TNS", StringComparison.OrdinalIgnoreCase)
                ? GetConnectionValue(conn, "tns_name")
                : BuildOracleBasicDataSource(conn);
            return builder.ConnectionString;
        }

        private static string BuildOracleBasicDataSource(Dictionary<string, object> conn)
        {
            string host = string.IsNullOrWhiteSpace(GetConnectionValue(conn, "host")) ? "localhost" : GetConnectionValue(conn, "host");
            string port = string.IsNullOrWhiteSpace(GetConnectionValue(conn, "port")) ? "1521" : GetConnectionValue(conn, "port");
            string value = GetConnectionValue(conn, "service_name");
            if (string.IsNullOrWhiteSpace(value)) value = GetConnectionValue(conn, "sid");
            string key = GetConnectionValue(conn, "oracle_identifier_type") == "sid" ? "SID" : "SERVICE_NAME";
            return "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=" + host + ")(PORT=" + port + "))" +
                   "(CONNECT_DATA=(" + key + "=" + value + ")))";
        }

        private static string GetConnectionValue(Dictionary<string, object> conn, string key)
        {
            if (conn != null && conn.ContainsKey(key) && conn[key] != null) return conn[key].ToString();
            return string.Empty;
        }

        private void mysqlStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenMySqlConnectionForm();
        }

        private void postgreSQLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenPostgreSqlConnectionForm();
        }

        private void oracleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenOracleConnectionForm();
        }

        private void sQLiteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenSqliteConnectionForm();
        }

        private void sQLServerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenSqlServerConnectionForm();
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
            Color bgColor = isSelected ? ThemeManager.ElevatedColor : ThemeManager.SurfaceColor;
            using (var brush = new SolidBrush(bgColor))
            {
                e.Graphics.FillRectangle(brush, tabRect);
            }

            // 文字 (置中偏左)
            Rectangle textRect = new Rectangle(tabRect.X + 4, tabRect.Y, tabRect.Width - 25, tabRect.Height);
            TextRenderer.DrawText(e.Graphics, title, queryTabs.Font, textRect, ThemeManager.TextColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            // 繪製關閉按鈕 (X)
            var xRect = GetCloseButtonRect(tabRect);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen p = new Pen(ThemeManager.MutedTextColor, 1.5f))
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
            bool canDock = GetDockableFromDrag(e.Data) != null && IsPointInTabDropArea(new Point(e.X, e.Y));
            if (canDock)
            {
                e.Effect = DragDropEffects.Move;
                queryTabs.Visible = true;
                queryTabs.BackColor = ThemeManager.SelectionColor;
                ShowDockHint();
                if (queryTabs.TabPages.Count == 0)
                {
                    statusStrip1.Visible = true; // 拖入時顯示提示
                    lblMainStatus.Text = Localization.T("Status.ReleaseToDock") + "...";
                }
            }
            else
                e.Effect = DragDropEffects.None;
        }

        private void QueryTabs_DragOver(object sender, DragEventArgs e)
        {
            bool canDock = GetDockableFromDrag(e.Data) != null && IsPointInTabDropArea(new Point(e.X, e.Y));
            if (canDock)
            {
                e.Effect = DragDropEffects.Move;
                ShowDockHint();
            }
            else
            {
                e.Effect = DragDropEffects.None;
                HideDockHint();
            }
        }

        private void QueryTabs_DragLeave(object sender, EventArgs e)
        {
            queryTabs.BackColor = ThemeManager.WindowBackColor;
            HideDockHint();
            if (queryTabs.TabPages.Count == 0) queryTabs.Visible = false;
            statusStrip1.Visible = false; // 離開時隱藏
            lblMainStatus.Text = Localization.T("Status.Ready");
        }

        private void QueryTabs_DragDrop(object sender, DragEventArgs e)
        {
            queryTabs.BackColor = ThemeManager.WindowBackColor;
            HideDockHint();
            statusStrip1.Visible = false; // 放下時隱藏
            lblMainStatus.Text = Localization.T("Status.Ready");
            var dockable = GetDockableFromDrag(e.Data);
            if (dockable != null && IsPointInTabDropArea(new Point(e.X, e.Y)))
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
