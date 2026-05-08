using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace mySQLPunk
{
    public static class Localization
    {
        public const string TraditionalChinese = "zh-TW";
        public const string English = "en-US";

        private static string _language = TraditionalChinese;
        private static readonly Dictionary<string, string[]> Texts = new Dictionary<string, string[]>();
        private static readonly Dictionary<string, string> CommonZhToEn = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> CommonEnToZh = new Dictionary<string, string>();

        static Localization()
        {
            Add("App.Title", "羽山的 mySQLPunk", "mySQLPunk");
            Add("Menu.File", "檔案", "File");
            Add("Menu.Edit", "編輯", "Edit");
            Add("Menu.View", "檢視", "View");
            Add("Menu.Favorites", "我的最愛", "Favorites");
            Add("Menu.Tools", "工具", "Tools");
            Add("Menu.Window", "視窗", "Window");
            Add("Menu.Help", "說明", "Help");
            Add("Menu.Language", "語言", "Language");
            Add("Menu.LanguageZh", "繁體中文", "Traditional Chinese");
            Add("Menu.LanguageEn", "English", "English");
            Add("Menu.Theme", "主題", "Theme");
            Add("Menu.ThemeLight", "亮色", "Light");
            Add("Menu.ThemeDark", "暗色", "Dark");
            Add("Menu.Options", "選項...", "Options...");
            Add("Menu.AddFavorite", "加入目前選取項目", "Add Current Selection");
            Add("Menu.NoFavorites", "尚無我的最愛", "No Favorites");
            Add("Menu.ClearFavorites", "清除我的最愛", "Clear Favorites");
            Add("Menu.NewConnection", "新增連線", "New Connection");
            Add("Menu.OpenConnection", "開啟連線", "Open Connection");
            Add("Menu.CloseConnection", "關閉連線", "Close Connection");
            Add("Menu.ExportConnections", "匯出連線", "Export Connections");
            Add("Menu.ImportConnections", "匯入連線", "Import Connections");
            Add("Menu.Close", "關閉", "Close");
            Add("Menu.Exit", "離開", "Exit");
            Add("Menu.About", "關於", "About");

            Add("Toolbar.Connection", "連線", "Connect");
            Add("Toolbar.NewQuery", "新增查詢", "New Query");
            Add("Toolbar.Table", "資料表", "Tables");
            Add("Toolbar.View", "檢視", "Views");
            Add("Toolbar.Function", "函式", "Functions");
            Add("Toolbar.User", "使用者", "Users");
            Add("Toolbar.Other", "其它", "Other");
            Add("Toolbar.Query", "查詢", "Queries");
            Add("Toolbar.Backup", "備份", "Backups");
            Add("Toolbar.AutoRun", "自動執行", "Automation");
            Add("Toolbar.Model", "模型", "Models");
            Add("Toolbar.BI", "BI", "BI");

            Add("Tool.OpenTable", "開啟資料表", "Open Table");
            Add("Tool.DesignTable", "設計資料表", "Design Table");
            Add("Tool.NewTable", "新增資料表", "New Table");
            Add("Tool.DeleteTable", "刪除資料表", "Delete Table");
            Add("Tool.ImportWizard", "匯入精靈", "Import Wizard");
            Add("Tool.ExportWizard", "匯出精靈", "Export Wizard");
            Add("Tool.OpenView", "開啟檢視", "Open View");
            Add("Tool.DesignView", "設計檢視", "Design View");
            Add("Tool.NewView", "新增檢視", "New View");
            Add("Tool.DeleteView", "刪除檢視", "Delete View");
            Add("Tool.DesignFunction", "設計函式", "Design Function");
            Add("Tool.NewFunction", "新增函式", "New Function");
            Add("Tool.DeleteFunction", "刪除函式", "Delete Function");
            Add("Tool.ExecuteFunction", "執行函式", "Execute Function");
            Add("Tool.EditConnection", "編輯連線", "Edit Connection");
            Add("Tool.DeleteConnection", "刪除連線", "Delete Connection");
            Add("Tool.SelectStar", "SELECT *", "SELECT *");
            Add("Tool.SelectAllColumns", "SELECT 全部欄位", "SELECT All Columns");
            Add("Tool.DumpSql", "傾印 SQL 檔案", "Dump SQL File");
            Add("Tool.CreateBackup", "建立備份", "Create Backup");
            Add("Tool.StructureAndData", "結構與資料", "Structure and Data");
            Add("Tool.DataOnly", "僅資料", "Data Only");
            Add("Tool.OpenQuery", "開啟查詢", "Open Query");
            Add("Tool.CloseQuery", "關閉查詢", "Close Query");
            Add("Tool.CopyObject", "複製物件", "Copy Object");
            Add("Tool.PasteObject", "貼上物件", "Paste Object");
            Add("Tool.RenameObject", "重新命名", "Rename");

            Add("Sidebar.Connections", "連線", "Connections");
            Add("Sidebar.ObjectDetails", "物件詳細資料", "Object Details");
            Add("Status.Ready", "就緒", "Ready");
            Add("Status.ReleaseToDock", "釋放滑鼠以嵌入視窗", "Release mouse to dock the window");
            Add("Status.LanguageChanged", "語言已切換。", "Language changed.");
            Add("Status.UserSelected", "使用者功能入口已選取。", "Users section selected.");
            Add("Status.OtherSelected", "其它功能入口已選取。", "Other section selected.");
            Add("Status.ModelSelected", "模型功能入口已選取。", "Models section selected.");
            Add("Status.BISelected", "BI 功能入口已選取。", "BI section selected.");
            Add("Status.SelectExpandedDatabase", "請先選取一個已展開的資料庫。", "Select an expanded database first.");
            Add("Status.SelectConnection", "請先選取一個連線。", "Select a connection first.");
            Add("Status.SelectTab", "請先選取要關閉的分頁。", "Select a tab to close first.");
            Add("Status.TabClosed", "分頁已關閉。", "Tab closed.");
            Add("Status.ConnectionClosed", "連線已斷開。", "Connection closed.");
            Add("Status.ConnectionAlreadyOpen", "連線已開啟：{0}", "Connection already open: {0}");
            Add("Status.ConnectionsExported", "連線設定已匯出。", "Connections exported.");
            Add("Status.ConnectionsImported", "連線設定已匯入。", "Connections imported.");
            Add("Status.ExportFailed", "匯出失敗：", "Export failed: ");
            Add("Status.ImportFailed", "匯入失敗：", "Import failed: ");
            Add("Status.ThemeChanged", "主題已切換。", "Theme changed.");
            Add("Status.OptionsApplied", "選項已套用。", "Options applied.");

            Add("Common.ConnectionName", "連線名稱", "Connection Name");
            Add("Common.ConnectionNameColon", "連線名稱:", "Connection Name:");
            Add("Common.Host", "主機 / IP", "Host / IP");
            Add("Common.HostName", "主機名稱 / IP 位址", "Host Name / IP Address");
            Add("Common.HostNameColon", "主機名稱 / IP 位址:", "Host Name/IP Address:");
            Add("Common.Port", "連接埠", "Port");
            Add("Common.PortColon", "連接埠:", "Port:");
            Add("Common.InitialDatabase", "初始資料庫", "Initial Database");
            Add("Common.InitialDatabaseColon", "初始資料庫:", "Initial Database:");
            Add("Common.Username", "使用者名稱", "User Name");
            Add("Common.UsernameColon", "使用者名稱:", "User Name:");
            Add("Common.Password", "密碼", "Password");
            Add("Common.PasswordColon", "密碼:", "Password:");
            Add("Common.TestConnection", "測試連線", "Test Connection");
            Add("Common.OK", "確定", "OK");
            Add("Common.Cancel", "取消", "Cancel");
            Add("Common.Next", "下一步", "Next");
            Add("Common.Browse", "瀏覽...", "Browse...");
            Add("Common.CreateNew", "建立新檔...", "Create New...");
            Add("Common.General", "一般", "General");
            Add("Common.Warning", "警告", "Warning");
            Add("Common.Error", "錯誤", "Error");
            Add("Common.Success", "成功", "Success");
            Add("Common.Info", "資訊", "Information");
            Add("Common.Complete", "完成", "Done");
            Add("Common.Confirm", "確認", "Confirm");
            Add("Common.WindowsAuth", "使用 Windows 驗證", "Use Windows Authentication");
            Add("Common.SQLiteFile", "SQLite 檔案", "SQLite File");
            Add("Common.InitGeospatial", "初始化 geospatial / SpatiaLite metadata", "Initialize geospatial / SpatiaLite metadata");
            Add("Common.ServiceName", "Service Name", "Service Name");
            Add("Common.ServiceNameSid", "Service Name / SID", "Service Name/SID");
            Add("Common.SID", "SID", "SID");
            Add("Common.ConnectionType", "連線類型", "Connection Type");
            Add("Common.NetServiceName", "Net Service Name", "Net Service Name");
            Add("Common.SQLiteConnection", "SQLite 連線", "SQLite Connection");
            Add("Common.SqlServerConnection", "SQL Server 連線", "SQL Server Connection");

            Add("ConnectionWizard.Title", "選取連線類型", "Select Connection Type");
            Add("ConnectionWizard.SelectType", "選取連線類型:", "Select connection type:");
            Add("ConnectionWizard.Recent", "最近使用過的", "Recent");
            Add("ConnectionWizard.All", "全部", "All");
            Add("ConnectionWizard.Search", "搜尋", "Search");
            Add("Connection.ExportTitle", "匯出連線設定", "Export Connections");
            Add("Connection.ImportTitle", "匯入連線設定", "Import Connections");
            Add("Connection.JsonFilter", "JSON 檔案 (*.json)|*.json|所有檔案 (*.*)|*.*", "JSON files (*.json)|*.json|All files (*.*)|*.*");
            Add("Connection.ImportReplaceConfirm", "匯入會取代目前所有連線設定，是否繼續？", "Importing will replace all current connection settings. Continue?");
            Add("Backup.Title", "建立備份", "Create Backup");
            Add("Backup.SelectDatabase", "請先選取一個已展開的資料庫或 Backups 節點。", "Select an expanded database or Backups node first.");
            Add("Backup.Success", "備份已建立。", "Backup created.");
            Add("Backup.Failed", "建立備份失敗：", "Backup failed: ");
            Add("ImportSql.Title", "匯入 SQL 檔案", "Import SQL File");
            Add("ImportSql.SelectDatabase", "請先選取一個已展開的資料庫。", "Select an expanded database first.");
            Add("ImportSql.Success", "SQL 匯入完成。執行語句數：{0}", "SQL import completed. Statements executed: {0}");
            Add("ImportSql.Failed", "匯入 SQL 失敗：", "SQL import failed: ");

            Add("Query.Query", "查詢", "Query");
            Add("Query.Execute", "執行", "Execute");
            Add("Query.Stop", "停止", "Stop");
            Add("Query.Beautify", "美化", "Beautify");
            Add("Query.Save", "儲存", "Save");
            Add("Query.Add", "新增", "Add");
            Add("Query.Delete", "刪除", "Delete");
            Add("Query.Refresh", "重新整理", "Refresh");
            Add("Query.Export", "匯出", "Export");
            Add("Query.Float", "浮動", "Float");
            Add("Query.Dock", "嵌入", "Dock");
            Add("Query.Results", "結果", "Results");
            Add("Query.Limit", " 筆數限制：", " Limit: ");
            Add("Query.Records", " 筆 ", " records ");
            Add("Query.PageFormat", " 第 {0} / {1} 頁 (總計：{2}) ", " Page {0} of {1} (Total: {2}) ");
            Add("Query.TableData", "資料表資料", "Table Data");

            Add("Designer.DesignTable", "設計資料表", "Design Table");
            Add("Designer.NewTable", "新增資料表", "New Table");
            Add("Designer.Save", "儲存", "Save");
            Add("Designer.AddColumn", "加入欄位", "Add Column");
            Add("Designer.InsertColumn", "插入欄位", "Insert Column");
            Add("Designer.DeleteColumn", "刪除欄位", "Delete Column");
            Add("Designer.AddIndex", "加入索引", "Add Index");
            Add("Designer.DeleteIndex", "刪除索引", "Delete Index");
            Add("Designer.MoveUp", "上移", "Move Up");
            Add("Designer.MoveDown", "下移", "Move Down");
            Add("Designer.Columns", "欄位", "Columns");
            Add("Designer.Indexes", "索引", "Indexes");
            Add("Designer.Options", "選項", "Options");
            Add("Designer.Comment", "註解", "Comment");
            Add("Designer.SqlPreview", "SQL 預覽", "SQL Preview");
            Add("Designer.ColumnProperties", "欄位屬性 (選取欄位以進行詳細設定)", "Column properties (select a column for details)");
            Add("Designer.TableName", "資料表名稱:", "Table Name:");
            Add("Designer.Engine", "引擎:", "Engine:");
            Add("Designer.Name", "名稱", "Name");
            Add("Designer.Type", "類型", "Type");
            Add("Designer.Length", "長度", "Length");
            Add("Designer.Decimals", "小數位數", "Decimals");
            Add("Designer.NotNull", "不是 Null", "Not Null");
            Add("Designer.PrimaryKey", "主鍵", "Primary Key");
            Add("Designer.Default", "預設", "Default");
            Add("Designer.IndexType", "索引類型", "Index Type");
            Add("Designer.IndexMethod", "索引方法", "Index Method");

            Add("Options.Title", "選項", "Options");
            Add("Options.General", "一般", "General");
            Add("Options.ThemeLabel", "佈景主題:", "Theme:");
            Add("Options.LanguageLabel", "語言:", "Language:");
            Add("Options.Light", "亮色", "Light");
            Add("Options.Dark", "深色", "Dark");
            Add("Options.Navigation", "導覽", "Navigation");
            Add("Options.AutoComplete", "自動完成程式碼", "Code Completion");
            Add("Options.Editor", "編輯器", "Editor");
            Add("Options.Record", "記錄", "Log");
            Add("Options.AI", "AI", "AI");
            Add("Options.AutoRecovery", "自動復原", "Auto Recovery");
            Add("Options.FileLocation", "檔案位置", "File Locations");
            Add("Options.Connection", "連線能力", "Connectivity");
            Add("Options.Environment", "環境", "Environment");
            Add("Options.Advanced", "進階", "Advanced");
            Add("Options.RestartNote", "* 部分已開啟視窗可能需要重新開啟才能完整套用。", "* Some open windows may need to be reopened to fully apply changes.");
        }

        public static string CurrentLanguage
        {
            get { return _language; }
        }

        public static bool IsEnglish
        {
            get { return _language == English; }
        }

        public static void Load()
        {
            try
            {
                string path = GetLanguageFilePath();
                if (File.Exists(path))
                {
                    SetLanguage(File.ReadAllText(path).Trim(), false);
                }
            }
            catch
            {
                _language = TraditionalChinese;
            }
        }

        public static void SetLanguage(string language, bool save)
        {
            _language = language == English ? English : TraditionalChinese;
            if (!save) return;

            try
            {
                string path = GetLanguageFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, _language);
            }
            catch
            {
            }
        }

        public static string T(string key)
        {
            string[] pair;
            if (!Texts.TryGetValue(key, out pair)) return key;
            return IsEnglish ? pair[1] : pair[0];
        }

        public static string Format(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }

        public static void ApplyTo(Control root)
        {
            if (root == null) return;
            ApplyControlText(root);
            ApplyToolStripItems(root as ToolStrip);

            foreach (Control child in root.Controls)
            {
                ApplyTo(child);
            }

        }

        public static string TranslateCommon(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string output;
            if (IsEnglish)
            {
                return CommonZhToEn.TryGetValue(text, out output) ? output : text;
            }
            return CommonEnToZh.TryGetValue(text, out output) ? output : text;
        }

        private static void ApplyControlText(Control control)
        {
            if (control is TextBox || control is ComboBox || control is RichTextBox || control is DataGridView)
            {
                return;
            }

            if (control is Form || control is Label || control is Button || control is CheckBox || control is RadioButton || control is GroupBox || control is TabPage)
            {
                control.Text = TranslateCommon(control.Text);
            }
        }

        private static void ApplyToolStripItems(ToolStrip strip)
        {
            if (strip == null) return;
            foreach (ToolStripItem item in strip.Items)
            {
                ApplyToolStripItem(item);
            }
        }

        private static void ApplyToolStripItem(ToolStripItem item)
        {
            if (!(item is ToolStripTextBox))
            {
                item.Text = TranslateCommon(item.Text);
            }

            ToolStripDropDownItem dropDown = item as ToolStripDropDownItem;
            if (dropDown == null) return;

            foreach (ToolStripItem child in dropDown.DropDownItems)
            {
                ApplyToolStripItem(child);
            }
        }

        private static void Add(string key, string zh, string en)
        {
            Texts[key] = new[] { zh, en };
            if (!CommonZhToEn.ContainsKey(zh)) CommonZhToEn[zh] = en;
            if (!CommonEnToZh.ContainsKey(en)) CommonEnToZh[en] = zh;
            if (!CommonZhToEn.ContainsKey(zh + ":")) CommonZhToEn[zh + ":"] = en + ":";
            if (!CommonEnToZh.ContainsKey(en + ":")) CommonEnToZh[en + ":"] = zh + ":";
        }

        private static string GetLanguageFilePath()
        {
            return Path.Combine(Application.UserAppDataPath, "language.txt");
        }
    }
}
