# mySQLPunk

mySQLPunk 是一個以 WinForms 開發的桌面資料庫管理工具，主要目標是提供連線管理、資料庫物件瀏覽、SQL 查詢、資料表設計與資料庫物件複製功能。

目前專案以 MySQL 支援最完整，PostgreSQL、SQLite 與 SQL Server 已具備部分查詢與物件操作能力。Oracle 目前只有連線 UI 雛形，尚未有可用的 provider 實作。

## 開發環境

必要環境：

- Windows
- Visual Studio 2017 以上，或 Visual Studio Build Tools
- .NET Framework 4.7.2 Developer Pack
- NuGet package restore

專案資訊：

- Solution: `mySQLPunk.sln`
- Project: `mySQLPunk/mySQLPunk.csproj`
- Target Framework: `.NET Framework 4.7.2`
- Output Type: `WinExe`

這是 .NET Framework WinForms 專案，不適合直接用 Linux 上的 `dotnet build` 完整建置。Linux 環境通常會因缺少 .NET Framework 4.7.2 reference assemblies 而失敗。

## 建置方式

在 Windows 開發機上：

```powershell
nuget restore .\mySQLPunk.sln
msbuild .\mySQLPunk.sln /p:Configuration=Debug /p:Platform="Any CPU"
```

或直接用 Visual Studio 開啟 `mySQLPunk.sln`，先還原 NuGet 套件，再建置 Debug / Any CPU。

## 主要功能狀態

| 功能 | 狀態 | 備註 |
| --- | --- | --- |
| 連線設定儲存 | 可用 | 設定儲存在執行目錄的 `setting.ini` |
| MySQL 連線與查詢 | 可用 | 目前最完整的 provider |
| PostgreSQL 連線與查詢 | 部分可用 | 已補基本 table/index/database metadata |
| SQLite 連線與查詢 | 部分可用 | 已加入 SpatiaLite runtime，並補基本 metadata |
| SQL Server 連線與查詢 | 部分可用 | 已補基本 table/index/database metadata |
| Oracle | 未完成 | 目前只有 UI 雛形，provider 尚未實作 |
| SQL 查詢視窗 | 可用 | 支援執行、語法高亮、自動補完、CSV 匯出 |
| 表格資料列編輯儲存 | 部分可用 | 目前支援 MySQL table data 模式 |
| MySQL Table Designer | 部分可用 | 支援既有 table 的 ALTER preview/save |
| New Table | 部分可用 | 已支援 MySQL `CREATE TABLE` preview/save |
| Table/View 複製 | 部分可用 | Table 可批次複製；View 僅支援同 provider |
| SQL Dump | 部分可用 | 目前只支援 MySQL table dump |

## 重要程式位置

- `mySQLPunk/Program.cs`: 應用程式入口
- `mySQLPunk/Form1.cs`: 主視窗、連線樹、工具列、dock/float tab、資料庫物件操作
- `mySQLPunk/QueryForm.cs`: SQL 編輯器與查詢結果視窗
- `mySQLPunk/TableDesignerForm.cs`: 資料表設計器
- `mySQLPunk/lib/IDatabase.cs`: database provider 介面
- `mySQLPunk/lib/my_mysql.cs`: MySQL provider
- `mySQLPunk/lib/my_postgresql.cs`: PostgreSQL provider
- `mySQLPunk/lib/my_sqlite.cs`: SQLite provider
- `mySQLPunk/lib/my_mssql.cs`: SQL Server provider
- `mySQLPunk/lib/DatabaseCopyService.cs`: Table/View 複製服務

## 待辦優先順序

1. 建立可重複的 Windows 建置與驗證流程。
2. 決定 Oracle 是要補實作，或暫時從 UI 隱藏。
3. 擴充非 MySQL 的 SQL dump 與 View/Table 設計能力。
4. 擴充非 MySQL 的資料列編輯儲存。
5. 針對 PostgreSQL、SQLite、SQL Server 補實機連線回歸測試。

## 驗證原則

每次修改後都需要依修改範圍做驗證：

- 文件或設定修改：檢查 diff、格式與指令範例。
- C# 程式碼修改：至少完成靜態檢查，並在可用 Windows 環境執行 build。
- UI 行為修改：在 Windows 上啟動程式，手動驗證主要操作流程。
- 資料庫 provider 修改：使用對應資料庫驗證連線、查詢、metadata 與錯誤處理。

若目前環境無法完整執行測試，提交說明與 PR 說明必須明確列出未執行項目與原因。
