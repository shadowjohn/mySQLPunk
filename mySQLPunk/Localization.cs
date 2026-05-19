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
            Add("Menu.ToolDataDictionary", "資料字典...", "Data Dictionary...");
            Add("Menu.ToolQueryHistory", "歷史記錄...", "History...");
            Add("Menu.ToolBackups", "備份...", "Backups...");
            Add("Menu.ToolConnectionDiagnostics", "連線診斷...", "Connection Diagnostics...");
            Add("Menu.ToolProviderCapabilities", "功能支援...", "Provider Capabilities...");
            Add("Menu.ToolMaintenanceChecklist", "維護檢查...", "Maintenance Checklist...");
            Add("Menu.AddFavorite", "加入目前選取項目", "Add Current Selection");
            Add("Menu.NoFavorites", "尚無我的最愛", "No Favorites");
            Add("Menu.ClearFavorites", "清除我的最愛", "Clear Favorites");
            Add("Menu.ConnectionProfile", "切換連線設定檔", "Switch Connection Profile");
            Add("Menu.CurrentProfile", "目前設定檔", "Current Profile");
            Add("Menu.Sort", "排序", "Sort");
            Add("Menu.SortAscending", "名稱遞增", "Name Ascending");
            Add("Menu.SortDescending", "名稱遞減", "Name Descending");
            Add("Menu.StarConnection", "加上星號", "Add Star");
            Add("Menu.UnstarConnection", "移除星號", "Remove Star");
            Add("Menu.NewGroup", "新增群組", "New Group");
            Add("Menu.Color", "色彩", "Color");
            Add("Menu.ColorDefault", "預設", "Default");
            Add("Menu.ColorRed", "紅色", "Red");
            Add("Menu.ColorOrange", "橘色", "Orange");
            Add("Menu.ColorYellow", "黃色", "Yellow");
            Add("Menu.ColorGreen", "綠色", "Green");
            Add("Menu.ColorBlue", "藍色", "Blue");
            Add("Menu.ColorPurple", "紫色", "Purple");
            Add("Menu.ManageGroups", "管理群組", "Manage Groups");
            Add("Menu.GroupName", "群組名稱", "Group Name");
            Add("Menu.GroupNamePrompt", "請輸入群組名稱：", "Enter a group name:");
            Add("Menu.MoveToGroup", "移至群組...", "Move to Group...");
            Add("Menu.RemoveFromGroup", "移出群組", "Remove from Group");
            Add("Menu.RenameGroup", "重新命名群組...", "Rename Group...");
            Add("Menu.DeleteGroup", "刪除群組", "Delete Group");
            Add("Menu.GroupCreated", "群組已建立：{0}", "Group created: {0}");
            Add("Menu.GroupDeleted", "群組已刪除：{0}（連線已移回根目錄）", "Group deleted: {0} (connections moved to root)");
            Add("Menu.GroupRenamed", "群組已重新命名：{0} → {1}", "Group renamed: {0} → {1}");
            Add("Menu.ConnectionMovedToGroup", "連線已移至群組：{0}", "Connection moved to group: {0}");
            Add("Menu.ConnectionRemovedFromGroup", "連線已移出群組", "Connection removed from group");
            Add("Menu.GroupNameRequired", "請輸入群組名稱。", "Please enter a group name.");
            Add("Menu.GroupNameExists", "群組名稱已存在：{0}", "Group name already exists: {0}");
            Add("Menu.ConfirmDeleteGroup", "確定要刪除群組「{0}」嗎？其中的連線將移回根目錄。", "Delete group \"{0}\"? Connections in this group will be moved to root.");
            Add("Menu.Share", "分享...", "Share...");
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
            Add("Tree.Tables", "資料表", "Tables");
            Add("Tree.Views", "檢視", "Views");
            Add("Tree.Functions", "函式", "Functions");
            Add("Tree.Users", "使用者", "Users");
            Add("Tree.Models", "模型", "Models");
            Add("Tree.BI", "BI", "BI");
            Add("Tree.Other", "其它", "Other");
            Add("Tree.Events", "事件", "Events");
            Add("Tree.Queries", "查詢", "Queries");
            Add("Tree.Reports", "報表", "Reports");
            Add("Tree.Backups", "備份", "Backups");

            Add("Tool.OpenTable", "開啟資料表", "Open Table");
            Add("Tool.DesignTable", "設計資料表", "Design Table");
            Add("Tool.FillAutoComments", "補註解", "Fill Comments");
            Add("Tool.FillBlankAutoComments", "補空白註解", "Fill Blank Comments");
            Add("Tool.OverwriteAutoComments", "覆蓋註解", "Overwrite Comments");
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
            Add("Tool.NewDatabase", "新增資料庫...", "New Database...");
            Add("Tool.CloseDatabase", "關閉資料庫", "Close Database");
            Add("Tool.EditDatabase", "編輯資料庫...", "Edit Database...");
            Add("Tool.DeleteDatabase", "刪除資料庫", "Delete Database");
            Add("Tool.CommandLine", "命令列介面...", "Command Line Interface...");
            Add("Tool.ExecuteSqlFile", "執行 SQL 檔案...", "Execute SQL File...");
            Add("Tool.DataDictionary", "資料字典...", "Data Dictionary...");
            Add("Tool.ReverseEngineerModel", "將資料庫逆向至模型...", "Reverse Engineer Database to Model...");
            Add("Tool.GenerateData", "資料產生...", "Data Generation...");
            Add("Tool.SqliteSpecialObjectWizard", "SQLite FTS / RTree / Spatial 精靈...", "SQLite FTS / RTree / Spatial Wizard...");
            Add("Tool.ExportSqliteColumnComments", "匯出 SQLite 欄位註解...", "Export SQLite Column Comments...");
            Add("Tool.ImportSqliteColumnComments", "匯入 SQLite 欄位註解...", "Import SQLite Column Comments...");
            Add("Tool.FindInDatabase", "在資料庫中尋找", "Find in Database");
            Add("Tool.OpenContainingFolder", "開啟所屬資料夾...", "Open Containing Folder...");
            Add("Tool.OpenExternalQuery", "開啟外部查詢...", "Open External Query...");
            Add("Tool.EditConnection", "編輯連線", "Edit Connection");
            Add("Tool.DeleteConnection", "刪除連線", "Delete Connection");
            Add("Tool.CopyConnection", "複製連線...", "Duplicate Connection...");
            Add("Tool.SelectStar", "SELECT *", "SELECT *");
            Add("Tool.SelectAllColumns", "SELECT 全部欄位", "SELECT All Columns");
            Add("Tool.DumpSql", "傾印 SQL 檔案", "Dump SQL File");
            Add("Tool.CreateBackup", "建立備份", "Create Backup");
            Add("Tool.RestoreBackup", "還原備份...", "Restore Backup...");
            Add("Tool.RestoreQuarantinedBackup", "還原隔離備份...", "Restore Quarantined Backup...");
            Add("Tool.StructureAndData", "結構與資料", "Structure and Data");
            Add("Tool.DataOnly", "僅資料", "Data Only");
            Add("Tool.OpenQuery", "開啟查詢", "Open Query");
            Add("Tool.CloseQuery", "關閉查詢", "Close Query");
            Add("Tool.OpenDetails", "開啟詳細資料", "Open Details");
            Add("Tool.CopyObject", "複製物件", "Copy Object");
            Add("Tool.PasteObject", "貼上物件", "Paste Object");
            Add("Tool.RenameObject", "重新命名", "Rename");

            Add("Sidebar.Connections", "連線", "Connections");
            Add("Sidebar.ObjectDetails", "物件詳細資料", "Object Details");
            Add("Status.Ready", "就緒", "Ready");
            Add("Status.LoadingData", "資料載入中...", "Loading data...");
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
            Add("Status.ConnectionListRefreshed", "連線清單已重新整理。", "Connection list refreshed.");
            Add("Status.BackupIntegrityPassed", "備份完整性定期驗證完成：{0} 個檔案通過。", "Scheduled backup integrity check completed: {0} files passed.");
            Add("Status.BackupIntegrityFailed", "備份完整性定期驗證發現 {0} 個異常檔案。", "Scheduled backup integrity check found {0} invalid files.");
            Add("Status.BackupIntegritySkipped", "備份完整性定期驗證已略過：目前沒有備份檔。", "Scheduled backup integrity check skipped: no backup files found.");
            Add("Status.BackupIntegrityReportPath", "報表：{0}", "Report: {0}");
            Add("Status.BackupIntegrityQuarantined", "已隔離 {0} 個異常備份到 {1}。", "{0} invalid backups quarantined to {1}.");
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
            Add("Common.Close", "關閉", "Close");
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
            Add("Common.None", "無", "None");
            Add("Common.SqlFilesFilter", "SQL 檔案 (*.sql)|*.sql|所有檔案 (*.*)|*.*", "SQL files (*.sql)|*.sql|All files (*.*)|*.*");
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
            Add("Connection.EnterConnectionName", "請輸入連線名稱。", "Enter a connection name.");
            Add("Connection.EnterHost", "請輸入主機名稱或 IP。", "Enter a host name or IP address.");
            Add("Connection.EnterPort", "請輸入連接埠。", "Enter a port.");
            Add("Connection.EnterUsername", "請輸入使用者名稱。", "Enter a user name.");
            Add("Connection.EnterUsernameOrWindowsAuth", "請輸入使用者名稱，或改用 Windows 驗證。", "Enter a user name, or use Windows Authentication.");
            Add("Connection.EnterNetServiceName", "請輸入 Net Service Name。", "Enter a Net Service Name.");
            Add("Connection.EnterServiceNameOrSid", "請輸入 Service Name 或 SID。", "Enter a Service Name or SID.");
            Add("Connection.MainWindowNotInitialized", "主視窗未初始化。", "The main window is not initialized.");
            Add("Connection.TestSucceeded", "{0} 連線成功。", "{0} connection succeeded.");
            Add("Connection.TestFailed", "{0} 連線失敗：{1}", "{0} connection failed: {1}");
            Add("Connection.InitializationFailed", "{0} 初始化失敗：{1}", "{0} initialization failed: {1}");
            Add("Connection.SelectOrCreateSqliteFile", "請選擇或建立 SQLite 檔案。", "Select or create a SQLite file.");
            Add("Connection.SqliteFileFilter", "SQLite database (*.sqlite;*.db;*.sqlite3)|*.sqlite;*.db;*.sqlite3|All files (*.*)|*.*", "SQLite database (*.sqlite;*.db;*.sqlite3)|*.sqlite;*.db;*.sqlite3|All files (*.*)|*.*");
            Add("Connection.SqliteNewFileFilter", "SQLite database (*.sqlite)|*.sqlite|SQLite DB (*.db)|*.db|All files (*.*)|*.*", "SQLite database (*.sqlite)|*.sqlite|SQLite DB (*.db)|*.db|All files (*.*)|*.*");
            Add("Connection.SpatiaLiteLoadFailed", "SQLite 可連線，但 SpatiaLite 載入失敗：\r\n{0}", "SQLite connected, but SpatiaLite failed to load:\r\n{0}");
            Add("Connection.InitSpatialMetadataTitle", "初始化 geospatial", "Initialize geospatial");
            Add("Connection.InitSpatialMetadataPrompt", "此 SQLite 檔尚未偵測到 SpatiaLite metadata，是否要初始化？", "This SQLite file does not have SpatiaLite metadata yet. Initialize it?");
            Add("Connection.UnsupportedEdit", "此連線類型尚未支援編輯：{0}", "Editing this connection type is not supported yet: {0}");
            Add("Connection.ConfirmDelete", "確定要刪除連線「{0}」嗎？", "Delete connection \"{0}\"?");
            Add("Connection.DeleteTitle", "刪除連線", "Delete Connection");
            Add("Connection.Duplicated", "已複製連線：{0}", "Connection duplicated: {0}");
            Add("Connection.ShareTitle", "分享連線", "Share Connection");
            Add("Connection.ShareFilter", "JSON 檔案 (*.json)|*.json|所有檔案 (*.*)|*.*", "JSON files (*.json)|*.json|All files (*.*)|*.*");
            Add("Connection.Shared", "連線分享檔已建立。密碼不會寫入分享檔。", "Connection share file created. Password is not included.");
            Add("Connection.ShareFailed", "分享連線失敗：{0}", "Share connection failed: {0}");
            Add("Connection.Refreshed", "連線已重新整理：{0}", "Connection refreshed: {0}");
            Add("Connection.DefaultProfile", "預設設定檔", "Default Profile");
            Add("Connection.CurrentProfileName", "目前設定檔：{0}", "Current Profile: {0}");
            Add("Connection.NewProfile", "新增連線設定檔...", "New Connection Profile...");
            Add("Connection.ProfileNamePrompt", "請輸入設定檔名稱：", "Enter a profile name:");
            Add("Connection.ProfileExists", "連線設定檔已存在：{0}", "Connection profile already exists: {0}");
            Add("Connection.ProfileCreated", "已建立並切換連線設定檔：{0}", "Connection profile created and switched: {0}");
            Add("Connection.ProfileSwitched", "已切換連線設定檔：{0}", "Connection profile switched: {0}");
            Add("Connection.CopyProfile", "複製目前設定檔...", "Copy Current Profile...");
            Add("Connection.RenameProfile", "重新命名設定檔...", "Rename Profile...");
            Add("Connection.DeleteProfile", "刪除目前設定檔", "Delete Current Profile");
            Add("Connection.ProfileCopied", "已複製連線設定檔：{0}", "Connection profile copied: {0}");
            Add("Connection.ProfileRenamed", "已重新命名連線設定檔：{0} → {1}", "Connection profile renamed: {0} → {1}");
            Add("Connection.ProfileDeleted", "已刪除連線設定檔：{0}，並切換回預設設定檔。", "Connection profile deleted: {0}. Switched back to the default profile.");
            Add("Connection.ConfirmDeleteProfile", "確定要刪除連線設定檔「{0}」嗎？此操作不會刪除預設 setting.ini。", "Delete connection profile \"{0}\"? This does not delete the default setting.ini.");
            Add("Connection.CommandLineUnavailable", "命令列介面目前尚未支援此連線類型。", "Command line interface is not supported for this connection type yet.");
            Add("Connection.CommandLineOpened", "已開啟命令列介面。", "Command line interface opened.");
            Add("Connection.CommandLineOpenFailed", "開啟命令列介面失敗：{0}", "Open command line interface failed: {0}");
            Add("Connection.CliNotFound", "找不到 {0} 命令列工具，請先安裝並確認已加入 PATH。\n說明：{1}", "Cannot find {0} CLI tool. Please install it and make sure it is added to PATH.\nHelp: {1}");
            Add("Connection.CliTemporaryPasswordPrompt", "此連線未儲存密碼。\n請輸入本次命令列介面要使用的密碼；留空會改由命令列工具自行提示。", "This connection does not have a saved password.\nEnter the password to use for this CLI session; leave blank to let the CLI prompt for it.");
            Add("Connection.CliTemporaryPasswordCancelled", "已取消開啟命令列介面。", "Opening command line interface cancelled.");
            Add("Connection.MarkedColor", "已標記連線色彩：{0}", "Connection color marked: {0}");

            Add("ConnectionWizard.Title", "選取連線類型", "Select Connection Type");
            Add("ConnectionWizard.SelectType", "選取連線類型:", "Select connection type:");
            Add("ConnectionWizard.Recent", "最近使用過的", "Recent");
            Add("ConnectionWizard.All", "全部", "All");
            Add("ConnectionWizard.Search", "搜尋", "Search");
            Add("ConnectionWizard.NoRecent", "尚無最近使用過的連線類型", "No recent connection types yet");
            Add("Connection.ExportTitle", "匯出連線設定", "Export Connections");
            Add("Connection.ImportTitle", "匯入連線設定", "Import Connections");
            Add("Connection.JsonFilter", "JSON 檔案 (*.json)|*.json|所有檔案 (*.*)|*.*", "JSON files (*.json)|*.json|All files (*.*)|*.*");
            Add("Connection.ImportReplaceConfirm", "匯入會取代目前所有連線設定，是否繼續？", "Importing will replace all current connection settings. Continue?");
            Add("Connection.ImportPasswordTitle", "補上匯入連線密碼", "Fill Imported Connection Passwords");
            Add("Connection.ImportPasswordPrompt", "偵測到 {0} 筆匯入連線沒有儲存密碼。要現在批次補上並存入 Windows Credential Manager 嗎？", "Detected {0} imported connections without saved passwords. Fill them now and store them in Windows Credential Manager?");
            Add("Connection.ImportPasswordDescription", "請在需要補密碼的連線列輸入密碼；留空的列會略過。密碼只會存入 Windows Credential Manager，不會寫入匯入檔。", "Enter passwords for the imported connections that need them; blank rows are skipped. Passwords are stored only in Windows Credential Manager, not in the import file.");
            Add("Connection.ImportPasswordSave", "儲存密碼", "Save Passwords");
            Add("Status.ConnectionsImportedWithPasswords", "連線設定已匯入，並已保存 {0} 筆密碼。", "Connections imported and {0} passwords saved.");
            Add("Connection.ImportPreviewTitle", "匯入連線差異預覽", "Connection Import Preview");
            Add("Connection.ImportPreviewSummary", "新增：{0}　更新：{1}　不變：{2}　只存在目前設定：{3}", "Added: {0}  Updated: {1}  Unchanged: {2}  Existing only: {3}");
            Add("Connection.ImportSignatureMissing", "來源簽章：無簽章（舊版或外部 JSON）", "Source signature: missing (legacy or external JSON)");
            Add("Connection.ImportSignatureValid", "有效", "valid");
            Add("Connection.ImportSignatureInvalid", "無效，內容可能已被修改", "invalid, content may have been modified");
            Add("Connection.ImportSignatureSummary", "來源簽章：{0}，SHA-256 {1}...", "Source signature: {0}, SHA-256 {1}...");
            Add("Connection.ImportSignatureSummaryWithTime", "來源簽章：{0}，SHA-256 {1}...，匯出時間 {2}", "Source signature: {0}, SHA-256 {1}..., exported at {2}");
            Add("Connection.ImportTrustKnown", "信任來源：已在白名單", "Trusted source: already trusted");
            Add("Connection.ImportTrustUnknown", "信任來源：尚未加入白名單", "Trusted source: not trusted yet");
            Add("Connection.ImportTrustMissingSignature", "信任來源：無簽章，無法辨識來源", "Trusted source: no signature, source cannot be identified");
            Add("Connection.ImportTrustSkippedInvalidSignature", "信任來源：簽章無效，已略過白名單判斷", "Trusted source: invalid signature, trust check skipped");
            Add("Connection.ImportTrustMissingSourceId", "信任來源：匯出檔沒有來源 ID（舊版匯出檔）", "Trusted source: no source ID in this export file (legacy export)");
            Add("Connection.ImportTrustSource", "信任此來源", "Trust This Source");
            Add("Connection.ImportSourceTrusted", "已將此匯出來源加入白名單。", "This export source has been added to the trusted list.");
            Add("Connection.ImportTrustSourceFailed", "加入信任來源失敗：{0}", "Failed to trust source: {0}");
            Add("Connection.ImportTrustInvalidSourceId", "來源 ID 無效。", "Invalid source ID.");
            Add("Connection.ImportTrustStoreUnavailable", "信任來源白名單路徑不可用。", "Trusted source store path is unavailable.");
            Add("Connection.ImportTrustConfirmMissingSignature", "這個匯入檔沒有來源簽章，無法確認內容是否被修改。仍要繼續匯入嗎？", "This import file has no source signature, so changes cannot be verified. Continue importing?");
            Add("Connection.ImportTrustConfirmInvalidSignature", "這個匯入檔的來源簽章無效，內容可能已被修改。仍要繼續匯入嗎？", "This import file has an invalid source signature and may have been modified. Continue importing?");
            Add("Connection.ImportTrustConfirmMissingSourceId", "這個匯入檔是舊版簽章格式，沒有來源 ID 可加入白名單。仍要繼續匯入嗎？", "This import file uses a legacy signature without a source ID. Continue importing?");
            Add("Connection.ImportTrustConfirmUnknown", "這個匯出來源尚未加入白名單。建議確認來源後先按「信任此來源」。仍要繼續匯入嗎？", "This export source is not trusted yet. Confirm the source or click Trust This Source first. Continue importing?");
            Add("Connection.ImportPreviewInclude", "合併", "Merge");
            Add("Connection.ImportPreviewStatus", "狀態", "Status");
            Add("Connection.ImportPreviewAdded", "新增", "Added");
            Add("Connection.ImportPreviewUpdated", "更新", "Updated");
            Add("Connection.ImportPreviewUnchanged", "不變", "Unchanged");
            Add("Connection.ImportPreviewExistingOnly", "只存在目前設定", "Existing Only");
            Add("Connection.ImportPreviewReplaceAll", "取代全部", "Replace All");
            Add("Connection.ImportPreviewMergeSelected", "合併勾選項目", "Merge Selected");
            Add("Connection.ImportPreviewSelectAtLeastOne", "請至少勾選一筆要合併的匯入連線。", "Select at least one imported connection to merge.");
            Add("Backup.Title", "建立備份", "Create Backup");
            Add("Backup.SelectDatabase", "請先選取一個已展開的資料庫或 Backups 節點。", "Select an expanded database or Backups node first.");
            Add("Backup.Success", "備份已建立。", "Backup created.");
            Add("Backup.SuccessVerified", "備份已建立並通過完整性驗證。", "Backup created and passed integrity verification.");
            Add("Backup.SuccessWithRemoteMirror", "備份已建立並通過完整性驗證。\n遠端備份副本：{0}", "Backup created and passed integrity verification.\nRemote backup copy: {0}");
            Add("Backup.Failed", "建立備份失敗：", "Backup failed: ");
            Add("Backup.IntegrityFailed", "備份完整性驗證失敗：{0}\n原因：{1}", "Backup integrity verification failed: {0}\nReason: {1}");
            Add("Backup.RestoreTitle", "還原備份", "Restore Backup");
            Add("Backup.RestoreFileFilter", "SQL / ZIP 備份 (*.sql;*.zip)|*.sql;*.zip|SQL 檔案 (*.sql)|*.sql|ZIP 備份 (*.zip)|*.zip|所有檔案 (*.*)|*.*", "SQL / ZIP backups (*.sql;*.zip)|*.sql;*.zip|SQL files (*.sql)|*.sql|ZIP backups (*.zip)|*.zip|All files (*.*)|*.*");
            Add("Backup.RestoreConfirm", "即將還原備份：\n{0}\n\nSQL 語句數：{1}\n大小：{2}\n目標資料庫：{3}\n\n還原會直接執行 SQL，可能覆蓋或刪除現有資料。是否繼續？", "Restore backup:\n{0}\n\nSQL statements: {1}\nSize: {2}\nTarget database: {3}\n\nRestore executes SQL directly and may overwrite or delete existing data. Continue?");
            Add("Backup.RestoreSuccess", "備份還原完成。執行語句數：{0}", "Backup restore completed. Statements executed: {0}");
            Add("Backup.RestoreSuccessWithSnapshot", "備份還原完成。執行語句數：{0}\n還原前快照：{1}", "Backup restore completed. Statements executed: {0}\nPre-restore snapshot: {1}");
            Add("Backup.RestoreSuccessWithSnapshotAndDiff", "備份還原完成。執行語句數：{0}\n還原前快照：{1}\n\n還原後差異檢查：\n{2}", "Backup restore completed. Statements executed: {0}\nPre-restore snapshot: {1}\n\nPost-restore diff check:\n{2}");
            Add("Backup.RestoreFailed", "還原備份失敗：{0}", "Backup restore failed: {0}");
            Add("Backup.RestoreSafetyBackupCreated", "還原前快照已建立：{0}", "Pre-restore snapshot created: {0}");
            Add("Backup.RestoreSafetyBackupFailedCancelled", "建立還原前快照失敗，已取消還原：{0}", "Failed to create pre-restore snapshot; restore canceled: {0}");
            Add("Backup.QuarantineRestoreTitle", "還原隔離備份", "Restore Quarantined Backup");
            Add("Backup.QuarantineRestoreFileFilter", "備份檔案 (*.sql;*.zip;*.sqlite;*.sqlite3;*.db)|*.sql;*.zip;*.sqlite;*.sqlite3;*.db|所有檔案 (*.*)|*.*", "Backup files (*.sql;*.zip;*.sqlite;*.sqlite3;*.db)|*.sql;*.zip;*.sqlite;*.sqlite3;*.db|All files (*.*)|*.*");
            Add("Backup.QuarantineRestoreEmpty", "目前沒有可還原的隔離備份。", "There are no quarantined backups to restore.");
            Add("Backup.QuarantineRestorePreviewConfirm", "隔離備份預覽：\n{0}\n\n原始路徑：{1}\n大小：{2}\n二次完整性驗證：{3}\n{4}\n\n此動作只會把檔案移回指定位置，不會直接還原到資料庫。是否繼續？", "Quarantined backup preview:\n{0}\n\nOriginal path: {1}\nSize: {2}\nSecond integrity check: {3}\n{4}\n\nThis only moves the file back to the selected location and does not restore it into the database. Continue?");
            Add("Backup.QuarantineRestoreIntegrityPassed", "通過", "Passed");
            Add("Backup.QuarantineRestoreIntegrityFailed", "未通過", "Failed");
            Add("Backup.QuarantineRestoreOverwrite", "目標檔案已存在：\n{0}\n\n是否覆蓋？", "The destination file already exists:\n{0}\n\nOverwrite it?");
            Add("Backup.QuarantineRestoreSuccess", "隔離備份已還原：\n{0}", "Quarantined backup restored:\n{0}");
            Add("Backup.QuarantineRestoreFailed", "還原隔離備份失敗：{0}", "Failed to restore quarantined backup: {0}");
            Add("Database.NewTitle", "新增資料庫", "New Database");
            Add("Database.NewPrompt", "資料庫名稱:", "Database name:");
            Add("Database.NameRequired", "請輸入資料庫名稱。", "Enter a database name.");
            Add("Database.UnsupportedCreate", "此連線類型不支援由主機節點新增資料庫：{0}", "Creating a database from the connection node is not supported for: {0}");
            Add("Database.OracleUnsupportedCreate", "Oracle 使用 User/Schema 概念，請使用新增 Oracle Schema 精靈建立使用者。", "Oracle uses the User/Schema concept. Use the Oracle schema wizard to create a user.");
            Add("Database.OracleCreateTitle", "新增 Oracle Schema", "New Oracle Schema");
            Add("Database.OracleUserName", "Schema / 使用者名稱:", "Schema / user name:");
            Add("Database.OraclePassword", "密碼:", "Password:");
            Add("Database.OracleDefaultTablespace", "預設 Tablespace:", "Default tablespace:");
            Add("Database.OracleTemporaryTablespace", "暫存 Tablespace:", "Temporary tablespace:");
            Add("Database.OracleUnlimitedQuota", "在預設 Tablespace 設定 UNLIMITED QUOTA", "Grant UNLIMITED QUOTA on default tablespace");
            Add("Database.OraclePasswordRequired", "請輸入 Oracle Schema 密碼。", "Enter the Oracle schema password.");
            Add("Database.SqliteUnsupportedCreate", "SQLite 資料庫是以檔案為單位，不支援由此介面新增資料庫。\n請直接建立新的 SQLite 連線，指向新的 .sqlite 檔案。", "SQLite databases are file-based and cannot be created from this interface.\nCreate a new SQLite connection pointing to a new .sqlite file.");
            Add("Database.Created", "資料庫已建立：{0}", "Database created: {0}");
            Add("Database.CreateFailed", "新增資料庫失敗：{0}", "Create database failed: {0}");
            Add("Database.Closed", "資料庫已關閉：{0}", "Database closed: {0}");
            Add("Database.EditOpened", "資料庫資訊已開啟：{0}", "Database information opened: {0}");
            Add("Database.ConfirmDelete", "確定要刪除資料庫「{0}」嗎？此操作不可還原！", "Delete database \"{0}\"? This action cannot be undone.");
            Add("Database.UnsupportedDelete", "此連線類型不支援由右鍵選單刪除資料庫：{0}", "Deleting a database from the context menu is not supported for: {0}");
            Add("Database.OracleUnsupportedDelete", "Oracle Schema 刪除需要具備 DROP USER 權限，且會使用 DROP USER CASCADE 刪除該 Schema 底下所有物件。", "Dropping an Oracle schema requires DROP USER permission and uses DROP USER CASCADE to delete every object under the schema.");
            Add("Database.OracleConfirmDropPrompt", "請輸入 Schema 名稱「{0}」以確認刪除：", "Type schema name \"{0}\" to confirm deletion:");
            Add("Database.OracleConfirmDropMismatch", "Schema 名稱不一致，已取消刪除。", "Schema name did not match. Delete cancelled.");
            Add("Database.OracleProtectedSchema", "不允許從此介面刪除 Oracle 系統 Schema：{0}", "Dropping Oracle system schema from this interface is not allowed: {0}");
            Add("Database.SqliteUnsupportedDelete", "SQLite 資料庫是以檔案為單位，不支援由此介面刪除資料庫。\n請直接刪除對應的 .sqlite 檔案。", "SQLite databases are file-based and cannot be deleted from this interface.\nDelete the corresponding .sqlite file directly.");
            Add("Database.SqliteConfirmDeleteFile", "即將先建立刪除前備份，然後關閉 SQLite 連線並將檔案移到資源回收筒：\n{0}\n\n刪除前備份：\n{1}\n\n若目前檔案系統不支援資源回收筒，將改為直接刪除。", "A pre-delete backup will be created first, then the SQLite connection will be closed and this file will be moved to the Recycle Bin:\n{0}\n\nPre-delete backup:\n{1}\n\nIf the current file system does not support the Recycle Bin, it will be deleted directly.");
            Add("Database.SqliteConfirmDeletePrompt", "請輸入檔名「{0}」以確認刪除：", "Type file name \"{0}\" to confirm deletion:");
            Add("Database.SqliteConfirmDeleteMismatch", "檔名不一致，已取消刪除。", "File name did not match. Delete cancelled.");
            Add("Database.SqlitePathMissing", "無法判斷 SQLite 資料庫檔案路徑。", "Cannot determine the SQLite database file path.");
            Add("Database.SqliteFileMissing", "SQLite 檔案不存在：{0}", "SQLite file does not exist: {0}");
            Add("Database.SqliteUnsafePath", "SQLite 檔案路徑不安全，已取消刪除：{0}", "SQLite file path is unsafe. Delete cancelled: {0}");
            Add("Database.Deleted", "資料庫已刪除：{0}", "Database deleted: {0}");
            Add("Database.DeletedWithBackup", "資料庫已刪除：{0}；刪除前備份：{1}", "Database deleted: {0}; pre-delete backup: {1}");
            Add("Database.PreDeleteBackupPrompt", "即將刪除資料庫「{0}」。是否要先建立刪除前 SQL 備份？\n\n備份位置：\n{1}\n\n選擇「是」會先備份再刪除；若備份失敗，刪除會中止。\n選擇「否」會不備份直接刪除。", "Database \"{0}\" is about to be deleted. Create a pre-delete SQL backup first?\n\nBackup path:\n{1}\n\nYes backs up first, then deletes. If backup fails, deletion is cancelled.\nNo deletes without backup.");
            Add("Database.PreDeleteBackupCreated", "刪除前備份已建立：{0}", "Pre-delete backup created: {0}");
            Add("Database.PreDeleteBackupFailedCancelled", "刪除前備份失敗，已取消刪除：{0}", "Pre-delete backup failed. Delete cancelled: {0}");
            Add("Database.SqliteBackupBeforeDeleteConnectionMissing", "無法取得 SQLite 連線，已取消刪除前備份。", "Cannot get the SQLite connection. Pre-delete backup cancelled.");
            Add("Database.DeleteFailed", "刪除資料庫失敗：{0}", "Delete database failed: {0}");
            Add("Database.AutoCommentsConfirm", "要掃描資料庫「{0}」的全部資料表，並補上空白欄位註解嗎？", "Scan every table in database \"{0}\" and fill blank column comments?");
            Add("Database.AutoCommentsConfirmOverwrite", "要掃描資料庫「{0}」的全部資料表，並覆蓋已有對照的欄位註解嗎？", "Scan every table in database \"{0}\" and overwrite matched column comments?");
            Add("Database.AutoCommentsScanning", "正在掃描資料表欄位...", "Scanning table columns...");
            Add("Database.AutoCommentsProgress", "補註解 {0}/{1}：{2}.{3}", "Filling comments {0}/{1}: {2}.{3}");
            Add("Database.AutoCommentsDone", "補註解完成：{0} 個欄位", "Fill comments done: {0} columns");
            Add("Database.AutoCommentsApplied", "已補上 {0} 個欄位註解。", "Filled {0} column comments.");
            Add("Database.AutoCommentsUpdated", "已更新 {0} 個欄位註解。", "Updated {0} column comments.");
            Add("Database.AutoCommentsNoUpdates", "沒有可更新的欄位註解。", "There are no column comments to update.");
            Add("Database.AutoCommentsFailed", "補註解失敗：{0}", "Fill comments failed: {0}");
            Add("Database.ModelOpened", "資料庫模型已建立：{0}", "Database model opened: {0}");
            Add("Database.SearchTitle", "在資料庫中尋找", "Find in Database");
            Add("Database.SearchPrompt", "搜尋文字:", "Search text:");
            Add("Database.SearchKeywordRequired", "請輸入要搜尋的文字。", "Enter text to search for.");
            Add("Database.SearchCompleted", "搜尋完成：{0} 筆結果", "Search completed: {0} result(s)");
            Add("Database.DataGenerationLoadFailed", "載入資料表清單失敗：{0}", "Failed to load table list: {0}");
            Add("Database.DataGenerationNoTables", "目前資料庫沒有可產生資料的資料表。", "There are no tables available for data generation.");
            Add("Database.GenerateDataTable", "資料表：", "Table:");
            Add("Database.GenerateDataRows", "筆數：", "Rows:");
            Add("Database.GenerateDataPreview", "產生 SQL", "Generate SQL");
            Add("Database.GenerateDataOpenQuery", "開啟查詢", "Open Query");
            Add("Database.GenerateDataExecute", "直接寫入", "Execute");
            Add("Database.DataGenerationHeader", "資料產生 SQL：{0}，筆數：{1}", "Data generation SQL: {0}, rows: {1}");
            Add("Database.DataGenerationNoColumns", "此資料表沒有可寫入的欄位。", "This table has no writable columns.");
            Add("Database.DataGenerationOpened", "資料產生 SQL 已開啟：{0}", "Data generation SQL opened: {0}");
            Add("Database.DataGenerationNoTarget", "無法取得資料產生目標資料庫。", "Cannot get the data generation target database.");
            Add("Database.DataGenerationNothingToExecute", "目前沒有可執行的 INSERT SQL。", "There is no executable INSERT SQL.");
            Add("Database.DataGenerationExecuteConfirm", "要直接寫入資料表「{0}」嗎？\r\n\r\n預計寫入筆數：{1}", "Execute inserts into table \"{0}\"?\r\n\r\nRows to insert: {1}");
            Add("Database.DataGenerationExecuting", "正在寫入資料：{0}/{1}", "Inserting data: {0}/{1}");
            Add("Database.DataGenerationExecuted", "資料已寫入：{0}，筆數：{1}", "Data inserted: {0}, rows: {1}");
            Add("Database.DataGenerationExecuteFailed", "資料寫入失敗：{0}", "Data generation execution failed: {0}");
            Add("Object.SelectDatabaseOrConnection", "請先選擇一個資料庫或連線！", "Select a database or connection first.");
            Add("Object.OpenConnectionFirst", "請先雙擊連線以開啟資料庫！", "Double-click the connection to open the database first.");
            Add("Object.SelectTable", "請先在左側選取一個具體的資料表 (Table)！", "Select a table in the left pane first.");
            Add("Object.SelectView", "請先在左側選取一個具體的檢視 (View)！", "Select a view in the left pane first.");
            Add("Object.SelectFunction", "請先在左側選取一個具體的函式或程序 (Function/Procedure)！", "Select a function or procedure in the left pane first.");
            Add("Object.SelectTableOrView", "請先選取單一 Table 或 View。", "Select one table or view first.");
            Add("Object.SelectCopyTarget", "請先選取目標 database 或其 Tables/Views 節點。", "Select a target database or its Tables/Views node first.");
            Add("Object.SelectViewTarget", "請先選取一個已展開的資料庫或 Views 節點。", "Select an expanded database or Views node first.");
            Add("Object.SelectFunctionTarget", "請先選取一個已展開的資料庫或 Functions 節點。", "Select an expanded database or Functions node first.");
            Add("Object.NoCopiedObject", "尚未複製任何 Table 或 View。", "No table or view has been copied yet.");
            Add("Object.SelectionConnectionUnavailable", "無法取得目前選取物件的連線資訊。", "Cannot get connection information for the selected object.");
            Add("Object.TargetNameExists", "目標名稱已存在：{0}", "Target name already exists: {0}");
            Add("Object.RenameFailed", "重新命名失敗：{0}", "Rename failed: {0}");
            Add("Object.CopyFailed", "複製失敗：{0}", "Copy failed: {0}");
            Add("Object.ViewCopyTitle", "跨 Provider 複製 View", "Cross-provider View Copy");
            Add("Object.ViewCopyPrompt", "來源（{0}）View：{1}\n目標 Provider：{2}\n\n選擇複製方式：", "Source ({0}) view: {1}\nTarget provider: {2}\n\nSelect copy mode:");
            Add("Object.ViewCopyAutoConvert", "嘗試轉換 View SQL（無法轉換時改用 table snapshot）", "Try converting View SQL (fall back to table snapshot if conversion fails)");
            Add("Object.ViewCopyForceSnapshot", "直接建立 Table snapshot（最穩定，不保留 View 語法）", "Create table snapshot directly (most stable, View SQL not preserved)");
            Add("Object.ViewCopySourceSql", "來源 View SQL", "Source View SQL");
            Add("Object.ViewCopyConvertedSql", "轉換後 SQL 預覽", "Converted SQL Preview");
            Add("Object.ViewCopyPreviewUnavailable", "無法產生安全轉換預覽：{0}", "Cannot generate a safe conversion preview: {0}");
            Add("Object.DeleteFailed", "刪除失敗：{0}", "Delete failed: {0}");
            Add("Object.UnknownError", "unknown error", "unknown error");
            Add("Object.ConfirmDeleteTable", "確定要刪除資料表「{0}」嗎？此操作不可還原！", "Delete table \"{0}\"? This action cannot be undone.");
            Add("Object.ConfirmDeleteView", "確定要刪除檢視「{0}」嗎？此操作不可還原！", "Delete view \"{0}\"? This action cannot be undone.");
            Add("Object.ConfirmDeleteFunction", "確定要刪除函式或程序「{0}」嗎？此操作不可還原！", "Delete function or procedure \"{0}\"? This action cannot be undone.");
            Add("Object.TableDeleted", "資料表已刪除。", "Table deleted.");
            Add("Object.ViewDeleted", "檢視已刪除。", "View deleted.");
            Add("Object.FunctionDeleted", "函式或程序已刪除。", "Function or procedure deleted.");
            Add("Object.SqliteNoStoredFunction", "SQLite 不支援資料庫內建 stored function。", "SQLite does not support built-in stored functions.");
            Add("Object.TableDeletedStatus", "資料表已刪除：{0}", "Table deleted: {0}");
            Add("Object.ViewDeletedStatus", "檢視已刪除：{0}", "View deleted: {0}");
            Add("Object.FunctionDeletedStatus", "函式或程序已刪除：{0}", "Function deleted: {0}");
            Add("Object.RenamedStatus", "已重新命名 {0}：{1} -> {2}", "Renamed {0}: {1} -> {2}");
            Add("Object.CopiedStatus", "已複製 {0}：{1}.{2}", "Copied {0}: {1}.{2}");
            Add("Object.CopyCompletedStatus", "複製完成：{0}", "Copy completed: {0}");
            Add("Object.CopyCompletedRowsStatus", "複製完成：{0} ({1} 筆)", "Copy completed: {0} ({1} rows)");
            Add("Object.SelectStarOpenedStatus", "SELECT * 已開啟：{0}", "SELECT * opened: {0}");
            Add("Object.SelectColumnsOpenedStatus", "SELECT 全部欄位已開啟：{0}", "SELECT columns opened: {0}");
            Add("Object.NewViewTemplateOpened", "新增檢視 SQL 範本已開啟。", "New view SQL template opened.");
            Add("Object.NewFunctionTemplateOpened", "新增函式 SQL 範本已開啟。", "New function SQL template opened.");
            Add("Object.FunctionExecutionOpened", "函式執行 SQL 已開啟：{0}", "Function execution SQL opened: {0}");
            Add("Object.SqlExported", "SQL 檔案已匯出。", "SQL file exported.");
            Add("Object.SqlExportFailed", "匯出 SQL 失敗：{0}", "SQL export failed: {0}");
            Add("Object.SqlDumpCreated", "SQL dump 已建立：{0}", "SQL dump created: {0}");
            Add("Object.SqlDumpCreatedFor", "SQL dump 已建立，目標 {0}：{1}", "SQL dump created for {0}: {1}");
            Add("Object.TableTarget", "資料表 {0}", "table {0}");
            Add("Object.ViewTarget", "檢視 {0}", "view {0}");
            Add("Object.DatabaseTarget", "資料庫 {0}", "database {0}");
            Add("Object.SqliteColumnCommentExchangeRequiresSqlite", "SQLite 欄位註解交換只支援 SQLite 連線。", "SQLite column comment exchange only supports SQLite connections.");
            Add("Object.SqliteColumnCommentJsonFilter", "SQLite 欄位註解 JSON (*.json)|*.json|所有檔案 (*.*)|*.*", "SQLite Column Comment JSON (*.json)|*.json|All Files (*.*)|*.*");
            Add("Object.SqliteColumnCommentsExported", "已匯出 {0} 個資料表、{1} 筆欄位註解。", "Exported {0} tables and {1} column comments.");
            Add("Object.SqliteColumnCommentsExportedStatus", "SQLite 欄位註解已匯出 {0} 筆：{1}", "SQLite column comments exported ({0}): {1}");
            Add("Object.SqliteColumnCommentsExportFailed", "匯出 SQLite 欄位註解失敗：{0}", "Export SQLite column comments failed: {0}");
            Add("Object.SqliteColumnCommentsImportConfirm", "即將匯入 {0} 個資料表、{1} 筆欄位註解，並覆蓋這些資料表目前的 mySQLPunk sidecar 註解。是否繼續？", "Import {0} tables and {1} column comments, replacing current mySQLPunk sidecar comments for those tables. Continue?");
            Add("Object.SqliteColumnCommentsImported", "已匯入 {0} 個資料表、{1} 筆欄位註解。", "Imported {0} tables and {1} column comments.");
            Add("Object.SqliteColumnCommentsImportedStatus", "SQLite 欄位註解已匯入 {0} 筆。", "SQLite column comments imported: {0}.");
            Add("Object.SqliteColumnCommentsImportFailed", "匯入 SQLite 欄位註解失敗：{0}", "Import SQLite column comments failed: {0}");
            Add("ImportSql.Title", "匯入 SQL 檔案", "Import SQL File");
            Add("ImportSql.SelectDatabase", "請先選取一個已展開的資料庫。", "Select an expanded database first.");
            Add("ImportSql.Success", "SQL 匯入完成。執行語句數：{0}", "SQL import completed. Statements executed: {0}");
            Add("ImportSql.Failed", "匯入 SQL 失敗：", "SQL import failed: ");

            Add("Query.Query", "查詢", "Query");
            Add("Query.Execute", "執行", "Execute");
            Add("Query.Stop", "停止", "Stop");
            Add("Query.Beautify", "美化", "Beautify");
            Add("Query.Save", "儲存", "Save");
            Add("Query.OpenSql", "開啟 SQL...", "Open SQL...");
            Add("Query.SaveSql", "儲存 SQL...", "Save SQL...");
            Add("Query.Cut", "剪下", "Cut");
            Add("Query.Copy", "複製", "Copy");
            Add("Query.CopySelectedCells", "複製選取儲存格", "Copy Selected Cells");
            Add("Query.CopyWithHeaders", "複製含欄位名稱", "Copy With Headers");
            Add("Query.CopySelectedRows", "複製選取資料列", "Copy Selected Rows");
            Add("Query.CopiedToClipboard", "已複製到剪貼簿。", "Copied to clipboard.");
            Add("Query.CopyGeometryAsWkt", "複製 Geometry 為 WKT", "Copy Geometry as WKT");
            Add("Query.CopyWktAsGeometrySql", "複製 WKT 為 Geometry SQL", "Copy WKT as Geometry SQL");
            Add("Query.GeometryToWktFailed", "無法將選取的 Geometry 轉成 WKT。", "Cannot convert the selected geometry to WKT.");
            Add("Query.WktRequired", "請選取 WKT 文字。", "Select WKT text first.");
            Add("Query.ViewBlobHex", "檢視 BLOB 十六進位", "View BLOB Hex");
            Add("Query.CopyBlobHex", "複製 BLOB Hex", "Copy BLOB Hex");
            Add("Query.SaveBlobFile", "匯出 BLOB 檔案", "Export BLOB File");
            Add("Query.ImportBlobFile", "匯入 BLOB 檔案", "Import BLOB File");
            Add("Query.BlobRequired", "請選取 BLOB / binary 欄位。", "Select a BLOB / binary cell first.");
            Add("Query.BlobFileFilter", "Binary files (*.bin)|*.bin|All files (*.*)|*.*", "Binary files (*.bin)|*.bin|All files (*.*)|*.*");
            Add("Query.BlobImportFileFilter", "All files (*.*)|*.*", "All files (*.*)|*.*");
            Add("Query.BlobSaved", "BLOB 已匯出：{0}", "BLOB exported: {0}");
            Add("Query.BlobImported", "BLOB 已匯入：{0}", "BLOB imported: {0}");
            Add("Grid.BinaryFormatFallback", "二進位欄位已改用文字方式顯示。", "Binary column displayed as text.");
            Add("Query.BlobPreviewTruncated", "僅顯示前 {0} bytes，完整大小：{1} bytes。", "Showing first {0} bytes only. Full size: {1} bytes.");
            Add("Query.BlobCopyPageHex", "複製本頁 Hex", "Copy Page Hex");
            Add("Query.BlobFirstPage", "首頁", "First");
            Add("Query.BlobPreviousPage", "上一頁", "Previous");
            Add("Query.BlobNextPage", "下一頁", "Next");
            Add("Query.BlobLastPage", "末頁", "Last");
            Add("Query.BlobPageFormat", "第 {0} / {1} 頁，bytes {2}-{3} / {4}", "Page {0} / {1}, bytes {2}-{3} / {4}");
            Add("Query.Paste", "貼上", "Paste");
            Add("Query.SelectAll", "全選", "Select All");
            Add("Query.SqlEditor", "SQL 編輯器", "SQL Editor");
            Add("Query.About", "關於查詢視窗", "About Query Window");
            Add("Query.Add", "新增", "Add");
            Add("Query.Delete", "刪除", "Delete");
            Add("Query.Refresh", "重新整理", "Refresh");
            Add("Query.Export", "匯出", "Export");
            Add("Query.ExportFileFilter", "Excel 活頁簿 (*.xlsx)|*.xlsx|CSV UTF-8 (*.csv)|*.csv|TSV (*.tsv)|*.tsv|JSON (*.json)|*.json|XML (*.xml)|*.xml|HTML 表格 (*.html)|*.html|Markdown (*.md)|*.md", "Excel Workbook (*.xlsx)|*.xlsx|CSV UTF-8 (*.csv)|*.csv|TSV (*.tsv)|*.tsv|JSON (*.json)|*.json|XML (*.xml)|*.xml|HTML Table (*.html)|*.html|Markdown (*.md)|*.md");
            Add("Query.ExportCompleted", "已匯出 {0} 筆資料到 {1}", "Exported {0} rows to {1}");
            Add("Query.ExportError", "匯出錯誤", "Export Error");
            Add("Query.Float", "浮動", "Float");
            Add("Query.Dock", "嵌入", "Dock");
            Add("Query.Results", "結果", "Results");
            Add("Query.Limit", " 筆數限制：", " Limit: ");
            Add("Query.Records", " 筆 ", " records ");
            Add("Query.PageFormat", " 第 {0} / {1} 頁 (總計：{2}) ", " Page {0} of {1} (Total: {2}) ");
            Add("Query.TableData", "資料表資料", "Table Data");
            Add("Query.SqlSaved", "SQL 已儲存：{0}", "SQL saved: {0}");
            Add("Query.SqlOpened", "SQL 已開啟：{0}", "SQL opened: {0}");
            Add("Query.ExternalOpened", "外部查詢已開啟：{0}", "External query opened: {0}");
            Add("Query.FolderOpened", "查詢資料夾已開啟：{0}", "Query folder opened: {0}");
            Add("Query.CountRowsFailed", "計算資料列數失敗：{0}", "Count rows failed: {0}");
            Add("Query.LoadingPleaseWait", "請稍候", "Please wait");
            Add("Query.LoadingTablePage", "正在載入資料表頁面...", "Loading table page...");
            Add("Query.Cancelled", "已取消。", "Cancelled.");
            Add("Query.LoadFailed", "載入失敗：{0}", "Load failed: {0}");
            Add("Query.LoadTableFailed", "載入資料表失敗：{0}", "Load table failed: {0}");
            Add("Query.PageSizeInvalid", "每頁筆數必須大於 0。", "Page size must be greater than 0.");
            Add("Query.Executing", "正在執行...", "Executing...");
            Add("Query.NoRowsStatus", "查無資料。", "No rows found.");
            Add("Query.NoRowsMessage", "查詢已完成，但沒有符合條件的資料。", "The query completed, but no rows matched the condition.");
            Add("Query.FeedbackTypeColumn", "狀態", "Status");
            Add("Query.FeedbackMessageColumn", "訊息", "Message");
            Add("Query.ErrorStatus", "錯誤：{0}", "Error: {0}");
            Add("Query.ExecuteError", "執行錯誤", "Execute Error");
            Add("Query.QueryError", "查詢錯誤", "Query Error");
            Add("Query.SaveTableDataOnly", "只有開啟資料表資料時才能儲存變更。", "Changes can only be saved when table data is open.");
            Add("Query.NoDataToSave", "目前沒有可儲存的資料。", "There is no data to save.");
            Add("Query.NoChangesDetected", "沒有偵測到資料變更。", "No data changes were detected.");
            Add("Query.ConfirmSaveChanges", "即將儲存目前資料表的新增、修改與刪除資料列，確定要繼續？", "Save inserted, updated, and deleted rows for the current table?");
            Add("Query.ConfirmSaveTitle", "確認儲存", "Confirm Save");
            Add("Query.NoPrimaryKeySaveWarningTitle", "資料表沒有 Primary Key", "Table Has No Primary Key");
            Add("Query.NoPrimaryKeySaveWarning", "目前資料表沒有 Primary Key，修改或刪除資料時會使用整列欄位組成 WHERE 條件。若資料列已被其他人修改，或包含 BLOB、浮點、大文字欄位，可能會更新失敗或影響非預期資料。仍要繼續儲存嗎？", "This table has no primary key. Updates and deletes will use the full row as the WHERE condition. If the row changed elsewhere, or contains BLOB, floating-point, or large text columns, saving may fail or affect unexpected data. Continue saving?");
            Add("Query.NoPrimaryKeyReadOnlyStatus", "此資料表沒有 Primary Key，已依選項以唯讀模式開啟。", "This table has no primary key and was opened read-only by option.");
            Add("Query.NoPrimaryKeyReadOnlyMessage", "此資料表沒有 Primary Key，目前已依選項設為唯讀，無法新增、修改、刪除或儲存。", "This table has no primary key and is read-only by option. Insert, update, delete, and save are disabled.");
            Add("Query.SavingChanges", "正在儲存變更...", "Saving changes...");
            Add("Query.SavedChangesStatus", "已儲存。新增：{0}，修改：{1}，刪除：{2}", "Saved. Inserted: {0}, Updated: {1}, Deleted: {2}");
            Add("Query.DataSaved", "資料已儲存。", "Data saved.");
            Add("Query.SaveFailed", "儲存失敗：{0}", "Save failed: {0}");
            Add("Query.CannotDetermineSaveTable", "無法判斷要儲存的資料表。", "Cannot determine which table to save.");
            Add("Query.NoWritableInsertColumns", "新增資料列沒有可寫入的欄位。", "The inserted row has no writable columns.");
            Add("Query.UnsafeWhereClause", "無法建立安全的 WHERE 條件。", "Cannot build a safe WHERE condition.");
            Add("Query.UnknownError", "未知錯誤", "Unknown error");

            Add("Designer.DesignTable", "設計資料表", "Design Table");
            Add("Designer.NewTable", "新增資料表", "New Table");
            Add("Designer.Save", "儲存", "Save");
            Add("Designer.ExecuteSql", "執行 SQL", "Execute SQL");
            Add("Designer.ExecuteSqlTooltip", "執行 SQL 預覽中的語法", "Execute SQL from the SQL preview.");
            Add("Designer.AddColumn", "加入欄位", "Add Column");
            Add("Designer.InsertColumn", "插入欄位", "Insert Column");
            Add("Designer.DeleteColumn", "刪除欄位", "Delete Column");
            Add("Designer.FillAutoComments", "補註解", "Fill Comments");
            Add("Designer.FillBlankAutoComments", "補空白註解", "Fill Blank Comments");
            Add("Designer.OverwriteAutoComments", "覆蓋註解", "Overwrite Comments");
            Add("Designer.ImportAutoCommentsDictionary", "匯入註解字典...", "Import Comment Dictionary...");
            Add("Designer.ExportAutoCommentsDictionary", "匯出註解字典...", "Export Comment Dictionary...");
            Add("Designer.SaveAutoCommentsDictionaryAs", "另存目前註解字典...", "Save Current Comment Dictionary As...");
            Add("Designer.SwitchAutoCommentsDictionary", "切換註解字典...", "Switch Comment Dictionary...");
            Add("Designer.RenameAutoCommentsDictionary", "重新命名註解字典...", "Rename Comment Dictionary...");
            Add("Designer.DeleteAutoCommentsDictionary", "刪除註解字典...", "Delete Comment Dictionary...");
            Add("Designer.CompareAutoCommentsDictionaryVersion", "比較註解字典版本...", "Compare Comment Dictionary Version...");
            Add("Designer.RestoreAutoCommentsDictionaryVersion", "回復註解字典版本...", "Restore Comment Dictionary Version...");
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
            Add("Designer.SelectDatabase", "請先選取一個已展開的資料庫或 Tables 節點。", "Select an expanded database or Tables node first.");
            Add("Designer.NewTableOpened", "新增資料表設計器已開啟。", "New table designer opened.");
            Add("Designer.Name", "名稱", "Name");
            Add("Designer.Type", "類型", "Type");
            Add("Designer.Length", "長度", "Length");
            Add("Designer.Decimals", "小數位數", "Decimals");
            Add("Designer.NotNull", "不是 Null", "Not Null");
            Add("Designer.PrimaryKey", "主鍵", "Primary Key");
            Add("Designer.Default", "預設", "Default");
            Add("Designer.IndexType", "索引類型", "Index Type");
            Add("Designer.IndexMethod", "索引方法", "Index Method");
            Add("Designer.SelectIndexColumns", "選擇索引欄位", "Select Index Columns");
            Add("Designer.ConfirmCloseChanges", "你要儲存對 {0} 的變更嗎？", "Save changes to {0}?");
            Add("Designer.UnsupportedExistingChanges", "目前不支援以下既有資料表變更：", "The following existing table changes are not currently supported:");
            Add("Designer.NoChangesDetected", "沒有偵測到變更。", "No changes detected.");
            Add("Designer.AutoCommentsUnavailable", "無法載入自動註解字典，請稍後再試。", "Cannot load the auto comment dictionary. Try again later.");
            Add("Designer.AutoCommentsLoadFailed", "無法載入自動註解字典：{0}", "Cannot load the auto comment dictionary: {0}");
            Add("Designer.AutoCommentsUnsupported", "目前連線類型不支援欄位註解，無法直接補註解。", "The current connection type does not support column comments, so comments cannot be filled directly.");
            Add("Designer.AutoCommentsApplied", "已補上 {0} 個欄位註解，請確認 SQL 預覽後儲存。", "Filled {0} column comments. Review the SQL preview, then save.");
            Add("Designer.AutoCommentsUpdated", "已更新 {0} 個欄位註解，請確認 SQL 預覽後儲存。", "Updated {0} column comments. Review the SQL preview, then save.");
            Add("Designer.AutoCommentsNoMatches", "沒有可補的空白欄位註解。", "There are no blank column comments to fill.");
            Add("Designer.AutoCommentsNoUpdates", "沒有可更新的欄位註解。", "There are no column comments to update.");
            Add("Designer.AutoCommentsLoading", "正在載入自動註解字典...", "Loading the auto comment dictionary...");
            Add("Designer.AutoCommentsDictionaryRemote", "已載入遠端自動註解字典。", "Loaded the remote auto comment dictionary.");
            Add("Designer.AutoCommentsDictionaryCache", "已使用本機快取自動註解字典。", "Using the local cached auto comment dictionary.");
            Add("Designer.AutoCommentsDictionaryImport", "已使用匯入的自動註解字典。", "Using the imported auto comment dictionary.");
            Add("Designer.AutoCommentsDictionaryNamed", "已使用「{0}」自動註解字典。", "Using the \"{0}\" auto comment dictionary.");
            Add("Designer.AutoCommentsDictionaryFileFilter", "自動註解字典 JSON (*.json)|*.json|所有檔案 (*.*)|*.*", "Auto Comment Dictionary JSON (*.json)|*.json|All Files (*.*)|*.*");
            Add("Designer.AutoCommentsImportSuccess", "已匯入 {0} 筆自動註解字典。", "Imported {0} auto comment dictionary entries.");
            Add("Designer.AutoCommentsImportFailed", "匯入自動註解字典失敗：{0}", "Import auto comment dictionary failed: {0}");
            Add("Designer.AutoCommentsImportEmpty", "自動註解字典沒有可用項目。", "The auto comment dictionary has no usable entries.");
            Add("Designer.AutoCommentsInvalidDictionaryFormat", "自動註解字典格式不正確。", "The auto comment dictionary format is invalid.");
            Add("Designer.AutoCommentsImportDiffSummary", "即將匯入 {0} 筆自動註解字典。\n\n新增：{1}\n更新：{2}\n匯入後會移除：{3}\n不變：{4}\n\n按確定後會覆蓋目前本機字典。", "Import {0} auto comment dictionary entries.\n\nAdded: {1}\nUpdated: {2}\nRemoved after import: {3}\nUnchanged: {4}\n\nClick OK to replace the current local dictionary.");
            Add("Designer.AutoCommentsDictionarySignatureMissing", "來源簽章：無簽章（舊版或外部 JSON）", "Source signature: missing (legacy or external JSON)");
            Add("Designer.AutoCommentsDictionarySignatureValid", "有效", "valid");
            Add("Designer.AutoCommentsDictionarySignatureInvalid", "無效，內容可能已被修改", "invalid, content may have been modified");
            Add("Designer.AutoCommentsDictionarySignatureSummary", "來源簽章：{0}，SHA-256 {1}...", "Source signature: {0}, SHA-256 {1}...");
            Add("Designer.AutoCommentsDictionarySignatureSummaryWithTime", "來源簽章：{0}，SHA-256 {1}...，匯出時間 {2}", "Source signature: {0}, SHA-256 {1}..., exported at {2}");
            Add("Designer.AutoCommentsDictionaryInvalidSignatureConfirm", "這個註解字典的來源簽章無效，內容可能已被修改。仍要繼續匯入並覆蓋目前本機字典嗎？", "This comment dictionary has an invalid source signature and may have been modified. Continue importing and replace the local dictionary?");
            Add("Designer.AutoCommentsImportDiffTitle", "註解字典差異預覽", "Comment Dictionary Diff Preview");
            Add("Designer.AutoCommentsImportApply", "套用匯入", "Apply Import");
            Add("Designer.AutoCommentsDiffStatus", "狀態", "Status");
            Add("Designer.AutoCommentsDiffKey", "欄位名稱", "Column");
            Add("Designer.AutoCommentsDiffExisting", "目前註解", "Current Comment");
            Add("Designer.AutoCommentsDiffImported", "匯入註解", "Imported Comment");
            Add("Designer.AutoCommentsDiffAdded", "新增", "Added");
            Add("Designer.AutoCommentsDiffUpdated", "更新", "Updated");
            Add("Designer.AutoCommentsDiffRemoved", "移除", "Removed");
            Add("Designer.AutoCommentsDiffUnchanged", "不變", "Unchanged");
            Add("Designer.AutoCommentsExportSuccess", "已匯出 {0} 筆自動註解字典。", "Exported {0} auto comment dictionary entries.");
            Add("Designer.AutoCommentsExportFailed", "匯出自動註解字典失敗：{0}", "Export auto comment dictionary failed: {0}");
            Add("Designer.AutoCommentsDictionaryNameTitle", "註解字典名稱", "Comment Dictionary Name");
            Add("Designer.AutoCommentsDictionaryNamePrompt", "請輸入要保存的註解字典名稱：", "Enter a name for the saved comment dictionary:");
            Add("Designer.AutoCommentsDictionaryNameRequired", "註解字典名稱不可空白。", "Comment dictionary name cannot be blank.");
            Add("Designer.AutoCommentsDictionarySaved", "已保存「{0}」註解字典，共 {1} 筆。", "Saved \"{0}\" comment dictionary with {1} entries.");
            Add("Designer.AutoCommentsDictionarySaveFailed", "保存註解字典失敗：{0}", "Save comment dictionary failed: {0}");
            Add("Designer.AutoCommentsNoSavedDictionaries", "目前沒有已保存的註解字典。", "There are no saved comment dictionaries.");
            Add("Designer.AutoCommentsDictionarySwitched", "已切換到「{0}」註解字典，共 {1} 筆。", "Switched to \"{0}\" comment dictionary with {1} entries.");
            Add("Designer.AutoCommentsDictionarySwitchFailed", "切換註解字典失敗：{0}", "Switch comment dictionary failed: {0}");
            Add("Designer.AutoCommentsDictionaryRenamed", "已將註解字典「{0}」重新命名為「{1}」。", "Renamed comment dictionary \"{0}\" to \"{1}\".");
            Add("Designer.AutoCommentsDictionaryRenameFailed", "重新命名註解字典失敗：{0}", "Rename comment dictionary failed: {0}");
            Add("Designer.AutoCommentsDictionaryDeleteConfirm", "確定要刪除註解字典「{0}」嗎？", "Delete comment dictionary \"{0}\"?");
            Add("Designer.AutoCommentsDictionaryDeleted", "已刪除註解字典「{0}」。", "Deleted comment dictionary \"{0}\".");
            Add("Designer.AutoCommentsDictionaryDeleteFailed", "刪除註解字典失敗：{0}", "Delete comment dictionary failed: {0}");
            Add("Designer.AutoCommentsDictionaryAlreadyExists", "註解字典「{0}」已存在。", "Comment dictionary \"{0}\" already exists.");
            Add("Designer.AutoCommentsDictionaryNotFound", "找不到指定的註解字典。", "The specified comment dictionary was not found.");
            Add("Designer.AutoCommentsDictionaryVersionRequired", "註解字典版本代號不可空白或包含特殊字元。", "Comment dictionary version id cannot be blank or contain special characters.");
            Add("Designer.AutoCommentsDictionaryVersionNotFound", "找不到指定的註解字典版本。", "The specified comment dictionary version was not found.");
            Add("Designer.AutoCommentsDictionaryNoVersions", "註解字典「{0}」目前沒有可比較或回復的歷史版本。", "Comment dictionary \"{0}\" does not have any saved versions to compare or restore.");
            Add("Designer.AutoCommentsDictionaryVersionListItem", "{0}，{1} 筆，版本 {2}", "{0}, {1} entries, version {2}");
            Add("Designer.AutoCommentsDictionaryVersionDiffTitle", "註解字典「{0}」版本差異", "Comment Dictionary \"{0}\" Version Diff");
            Add("Designer.AutoCommentsDictionaryVersionRestoreTitle", "回復註解字典「{0}」版本", "Restore Comment Dictionary \"{0}\" Version");
            Add("Designer.AutoCommentsDictionaryVersionRestoreApply", "回復版本", "Restore Version");
            Add("Designer.AutoCommentsDictionaryVersionRestored", "已回復「{0}」註解字典，共 {1} 筆。", "Restored \"{0}\" comment dictionary with {1} entries.");
            Add("Designer.AutoCommentsDictionaryVersionCompareFailed", "比較註解字典版本失敗：{0}", "Compare comment dictionary version failed: {0}");
            Add("Designer.AutoCommentsDictionaryVersionRestoreFailed", "回復註解字典版本失敗：{0}", "Restore comment dictionary version failed: {0}");
            Add("Designer.AutoCommentsProgress", "補註解 {0}/{1}：{2}", "Filling comments {0}/{1}: {2}");
            Add("Designer.AutoCommentsDone", "補註解完成：{0}/{1}", "Fill comments done: {0}/{1}");
            Add("Designer.EnterTableNameInOptions", "請先在「選項」分頁輸入資料表名稱。", "Enter a table name on the Options tab first.");
            Add("Designer.AddAtLeastOneColumn", "請至少新增一個欄位。", "Add at least one column.");
            Add("Designer.KeepAtLeastOneColumn", "請至少保留一個欄位。", "Keep at least one column.");
            Add("Designer.SqliteColumnCommentUnsupported", "SQLite 欄位註解會保存到 mySQLPunk sidecar metadata：{0}", "SQLite column comments are saved to mySQLPunk sidecar metadata: {0}");
            Add("SqliteWizard.Title", "SQLite 專用物件精靈", "SQLite Special Object Wizard");
            Add("SqliteWizard.Kind", "類型", "Type");
            Add("SqliteWizard.Fts", "FTS5 全文搜尋 virtual table", "FTS5 full-text virtual table");
            Add("SqliteWizard.RTree", "RTree virtual table", "RTree virtual table");
            Add("SqliteWizard.SpatialIndex", "SpatiaLite spatial index", "SpatiaLite spatial index");
            Add("SqliteWizard.ObjectName", "物件名稱", "Object name");
            Add("SqliteWizard.Columns", "欄位", "Columns");
            Add("SqliteWizard.Tokenizer", "Tokenizer", "Tokenizer");
            Add("SqliteWizard.ContentTable", "Content table", "Content table");
            Add("SqliteWizard.TargetTable", "目標資料表", "Target table");
            Add("SqliteWizard.GeometryColumn", "Geometry 欄位", "Geometry column");
            Add("SqliteWizard.SqlPreview", "SQL 預覽", "SQL Preview");
            Add("SqliteWizard.NoSql", "目前沒有可執行的 SQL。", "There is no executable SQL.");
            Add("SqliteWizard.ExecuteSucceeded", "SQLite 專用物件 SQL 執行成功。", "SQLite special object SQL executed successfully.");
            Add("SqliteWizard.ExecuteFailed", "SQLite 專用物件 SQL 執行失敗：{0}", "SQLite special object SQL failed: {0}");
            Add("Designer.PrimaryKeyNeedsConstraintName", "PRIMARY KEY 修改需要資料庫特定 constraint 名稱：{0}", "PRIMARY KEY changes require a database-specific constraint name: {0}");
            Add("Designer.PrimaryKeyMissingColumns", "PRIMARY KEY 缺少欄位：{0}", "PRIMARY KEY is missing columns: {0}");
            Add("Designer.FullTextUnsupported", "此資料庫尚未支援 FULLTEXT 索引：{0}", "This database does not currently support FULLTEXT indexes: {0}");
            Add("Designer.SpatialUnsupported", "此資料庫尚未支援 SPATIAL 索引：{0}", "This database does not currently support SPATIAL indexes: {0}");
            Add("Designer.NoSqlToExecute", "請先在 SQL 預覽輸入要執行的 SQL。", "Enter SQL in the SQL preview first.");
            Add("Designer.ConfirmExecuteSqlTitle", "確認執行 SQL", "Confirm SQL Execution");
            Add("Designer.ExecuteSqlSucceeded", "SQL 執行成功。", "SQL executed successfully.");
            Add("Designer.ExecuteSqlFailedTitle", "SQL 執行失敗", "SQL Execution Failed");
            Add("Designer.CannotSaveTitle", "無法儲存", "Cannot Save");
            Add("Designer.ConfirmSaveTitle", "確認儲存", "Confirm Save");
            Add("Designer.ConfirmExecuteSql", "即將執行以下 SQL：\n\n{0}\n\n確定嗎？", "The following SQL will be executed:\n\n{0}\n\nContinue?");
            Add("Designer.SaveSucceeded", "儲存成功。", "Save succeeded.");
            Add("Designer.SaveFailedTitle", "儲存失敗", "Save Failed");
            Add("Designer.SaveFailedReason", "儲存失敗：{0}", "Save failed: {0}");
            Add("Designer.FailedSql", "失敗 SQL：", "Failed SQL:");
            Add("Designer.OracleDiagnosticTitle", "Oracle Table Designer 診斷：", "Oracle Table Designer Diagnostics:");
            Add("Designer.OraclePreviewNoticeTitle", "Oracle Table Designer 預覽提醒：", "Oracle Table Designer Preview Notes:");
            Add("Designer.OraclePreviewObjectHint", "目標物件：{0}.{1}；若其他人剛修改、刪除或重新命名，請先重新整理左側資料庫樹後再執行。", "Target object: {0}.{1}; if someone just changed, deleted, or renamed it, refresh the database tree before executing.");
            Add("Designer.OraclePreviewPrivilegeHint", "請確認目前帳號具備 ALTER、CREATE INDEX、DROP、COMMENT 等直接授權；Oracle 的 role 權限在部分 DDL 情境可能不會生效。", "Confirm that the current account has direct grants for ALTER, CREATE INDEX, DROP, COMMENT, and other required DDL; Oracle role privileges may not apply in some DDL contexts.");
            Add("Designer.OraclePreviewStepHint", "儲存時會依 SQL 預覽分段執行；PL/SQL block 會維持為單一段落，方便定位失敗步驟。", "Saving executes the SQL preview in separate steps; PL/SQL blocks stay grouped as a single step so failures are easier to locate.");
            Add("Designer.CurrentSchema", "目前 schema", "current schema");
            Add("Designer.CurrentTable", "目前資料表", "current table");
            Add("Designer.OracleHintInsufficientPrivileges", "目前帳號沒有足夠權限執行這個 DDL。請確認已直接授權 ALTER、CREATE TABLE、CREATE VIEW、CREATE INDEX、DROP 或 COMMENT 等需要的權限；Oracle 的 role 權限在部分 DDL 情境可能不會生效。", "The current account does not have enough privileges to execute this DDL. Confirm that ALTER, CREATE TABLE, CREATE VIEW, CREATE INDEX, DROP, COMMENT, or other required privileges are granted directly; Oracle role privileges may not apply in some DDL contexts.");
            Add("Designer.OracleHintCrossSchemaPrivileges", "若要修改其他 schema 的物件，請確認 {0}.{1} 的 ALTER/INDEX 權限已授給目前登入帳號。", "To modify an object in another schema, confirm that ALTER/INDEX privileges on {0}.{1} are granted to the current login.");
            Add("Designer.OracleHintObjectMissing", "Oracle 找不到目標物件，或目前帳號沒有存取權限。請確認 {0}.{1} 仍存在，並具備 SELECT/ALTER 權限。", "Oracle cannot find the target object, or the current account cannot access it. Confirm that {0}.{1} still exists and that SELECT/ALTER privileges are available.");
            Add("Designer.OracleHintRefreshAfterObjectChange", "若物件剛被其他人刪除或重新命名，請重新整理左側資料庫樹後再開啟 Table Designer。", "If the object was just deleted or renamed by someone else, refresh the database tree and reopen Table Designer.");
            Add("Designer.OracleHintNameConflict", "目標名稱已存在，通常代表欄位、索引或暫存物件和現有名稱衝突。請重新整理欄位/索引清單，確認沒有重複名稱後再儲存。", "The target name already exists, usually because a column, index, or temporary object conflicts with an existing name. Refresh the column/index list, confirm there are no duplicate names, then save again.");
            Add("Designer.OracleHintNullStateChanged", "欄位 NULL/NOT NULL 狀態和目前資料庫狀態不一致，可能是其他人已先修改欄位。請重新載入 Table Designer 後再套用變更。", "The column NULL/NOT NULL state no longer matches the database, possibly because someone else changed the column first. Reload Table Designer before applying the change.");
            Add("Designer.OracleHintNotNullConflict", "欄位要改成 NOT NULL，但既有資料可能包含 NULL。請先清理資料或設定預設值，再重新儲存。", "The column is being changed to NOT NULL, but existing data may contain NULL values. Clean the data or set a default value before saving again.");
            Add("Designer.OracleHintConstraintIndexConflict", "正在刪除或修改被主鍵/唯一約束使用的索引。請先處理對應 constraint，再調整索引。", "An index used by a primary key or unique constraint is being deleted or modified. Handle the related constraint first, then adjust the index.");
            Add("Designer.OracleHintAlterSyntax", "產生的 ALTER TABLE 語法不符合目前 Oracle 版本或物件型態。請檢查 SQL 預覽，或改用分段 SQL 手動調整。", "The generated ALTER TABLE syntax does not match the current Oracle version or object type. Check the SQL preview or adjust it manually with staged SQL.");
            Add("Designer.OracleHintGeneric", "請檢查目前帳號對 {0}.{1} 的 DDL 權限、物件是否仍存在，以及 SQL 預覽中的語法是否符合 Oracle 限制。", "Check the current account's DDL privileges on {0}.{1}, whether the object still exists, and whether the SQL preview syntax matches Oracle limitations.");

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
            Add("Options.NoPrimaryKeyReadOnly", "沒有 Primary Key 的資料表以唯讀模式開啟", "Open tables without a primary key as read-only");
            Add("Options.CliPathHint", "可指定各資料庫命令列工具的位置。留空時會使用 PATH，SQLite 會優先使用內建 sqlite3.exe。", "Set database CLI executable paths. Leave blank to use PATH; SQLite uses the bundled sqlite3.exe first.");
            Add("Options.CliPathMySql", "MySQL:", "MySQL:");
            Add("Options.CliPathPostgreSql", "PostgreSQL:", "PostgreSQL:");
            Add("Options.CliPathSqlServer", "SQL Server:", "SQL Server:");
            Add("Options.CliPathOracle", "Oracle:", "Oracle:");
            Add("Options.CliPathSqlite", "SQLite:", "SQLite:");
            Add("Options.BackupMirrorHint", "可指定備份完成後要同步複製的遠端或共享資料夾，例如 NAS、雲端同步資料夾或團隊共享磁碟。留空時不會建立遠端副本。", "Set a remote or shared folder to copy backups after they are created, such as a NAS, cloud-synced folder, or team share. Leave blank to skip remote copies.");
            Add("Options.BackupMirrorDirectory", "遠端備份資料夾:", "Remote backup folder:");
            Add("Options.BackupMirrorRetainCount", "遠端保留份數:", "Remote copies to keep:");
            Add("Options.BackupIntegrityScheduleEnabled", "定期驗證備份完整性", "Run scheduled backup integrity checks");
            Add("Options.BackupIntegrityIntervalHours", "驗證間隔（小時）:", "Check interval (hours):");
            Add("Options.BackupIntegrityAutoQuarantine", "驗證失敗時自動隔離異常備份", "Automatically quarantine invalid backups");
            Add("Options.BackupIntegrityQuarantineRetainCount", "隔離區保留份數:", "Quarantine files to keep:");
            Add("Options.ExecutableFilter", "執行檔 (*.exe)|*.exe|所有檔案 (*.*)|*.*", "Executable files (*.exe)|*.exe|All files (*.*)|*.*");
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
