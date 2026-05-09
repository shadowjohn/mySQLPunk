# mySQLPunk

mySQLPunk 是一個以 WinForms 開發的桌面資料庫管理工具，主要目標是提供連線管理、資料庫物件瀏覽、SQL 查詢、資料表設計與資料庫物件複製功能。

目前專案以 MySQL 支援最完整，PostgreSQL、SQLite 與 SQL Server 已具備部分查詢與物件操作能力。Oracle 已具備基本連線、查詢與 metadata 瀏覽能力，進階設計與複製行為仍需實機回歸。

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
| PostgreSQL 連線與查詢 | 可用 | 已補 table/index/database metadata 與 table data 分頁 |
| SQLite 連線與查詢 | 可用 | 已加入 SpatiaLite runtime，並補 metadata 與 table data 分頁 |
| SQL Server 連線與查詢 | 可用 | 已補 table/index/database metadata 與 table data 分頁 |
| Oracle | 部分可用 | 已補基本連線、查詢、schema/table/view metadata、table data 分頁與同 provider View 複製 |
| SQL 查詢視窗 | 可用 | 支援執行、語法高亮、自動補完、CSV 匯出；資料表右鍵 `SELECT *` 與 `SELECT 全部欄位` 皆會進入可分頁與編輯的資料表資料模式 |
| 表格資料列編輯儲存 | 可用 | table data 模式已支援 MySQL、PostgreSQL、SQLite、SQL Server、Oracle；更新與刪除會優先使用 Primary Key 條件 |
| Table Designer | 部分可用 | 既有 table ALTER 已支援 MySQL、PostgreSQL、SQL Server、Oracle；PostgreSQL、SQL Server 與 Oracle 已支援既有 Primary Key 更新；PostgreSQL 已支援 FULLTEXT/GIN 與 SPATIAL/GiST 索引建立；Oracle 已支援 SDO_GEOMETRY 與 SPATIAL/MDSYS.SPATIAL_INDEX 索引建立；SQL Server 已支援既有欄位 DEFAULT constraint 與欄位註解更新；Oracle 儲存失敗會提供權限與物件狀態診斷；SQLite 已支援以安全重建流程處理欄位新增、刪除、重新命名、排序、型別、NULL、DEFAULT 與 Primary Key 變更 |
| New Table | 可用 | 已支援 MySQL、PostgreSQL、SQLite、SQL Server、Oracle `CREATE TABLE` preview/save |
| Table/View 複製 | 可用 | Table 可批次複製；同 provider View 複製為 View；異種 provider View 會先嘗試轉換 SQL，無法安全轉換時改為 table snapshot |
| SQL Dump | 可用 | Table dump 已支援 MySQL、PostgreSQL、SQLite、SQL Server、Oracle；Oracle dump 會輸出 schema-qualified INSERT、HEXTORAW 與 TO_TIMESTAMP literal |

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
- `mySQLPunk/lib/my_oracle.cs`: Oracle provider
- `mySQLPunk/lib/DatabaseCopyService.cs`: Table/View 複製服務

## 待辦優先順序

1. 建立可重複的 Windows 建置與驗證流程。
2. 針對 Oracle、PostgreSQL、SQLite、SQL Server 補實機連線回歸測試。

## 驗證原則

每次修改後都需要依修改範圍做驗證：

- 文件或設定修改：檢查 diff、格式與指令範例。
- C# 程式碼修改：至少完成靜態檢查，並在可用 Windows 環境執行 build。
- UI 行為修改：在 Windows 上啟動程式，手動驗證主要操作流程。
- 資料庫 provider 修改：使用對應資料庫驗證連線、查詢、metadata 與錯誤處理。

若目前環境無法完整執行測試，提交說明與 PR 說明必須明確列出未執行項目與原因。
