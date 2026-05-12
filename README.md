# mySQLPunk

mySQLPunk 是一套 Windows WinForms 資料庫管理工具，目標是用同一個介面管理多種資料庫連線、瀏覽資料表與檢視、執行 SQL、編輯資料、設計資料表、搬移 Table/View，以及產生常用 DDL/DML。

目前主要支援 MySQL、PostgreSQL、SQLite、SQL Server，Oracle 支援已逐步補齊但仍有部分限制。

## 開發環境

- Windows
- Visual Studio 2017 或更新版本，或 Visual Studio Build Tools
- .NET Framework 4.7.2 Developer Pack
- NuGet package restore

專案資訊：

- Solution: `mySQLPunk.sln`
- Project: `mySQLPunk/mySQLPunk.csproj`
- Target Framework: `.NET Framework 4.7.2`
- Output Type: `WinExe`

建置：

```powershell
nuget restore .\mySQLPunk.sln
msbuild .\mySQLPunk.sln /p:Configuration=Debug /p:Platform="Any CPU"
```

## 目前功能概況

| 功能 | 狀態 | 說明 |
| --- | --- | --- |
| 連線管理 | 可用 | 預設連線資訊儲存在 `setting.ini`，並支援切換多個連線設定檔。 |
| MySQL | 可用 | 主要 provider，支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer。 |
| PostgreSQL | 可用 | 支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；部分進階索引仍有限制。 |
| SQLite | 可用 | 支援一般 SQLite 與 SpatiaLite 載入；SQLite 本身不支援欄位註解。 |
| SQL Server | 可用 | 支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；部分 DEFAULT constraint 與進階索引仍有限制。 |
| Oracle | 部分可用 | 支援 schema/table/view metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；部分 DDL 仍受權限、語法與物件型態限制。 |
| SQL 查詢 | 可用 | 支援 SELECT/SHOW/EXPLAIN/DESC/WITH 類結果顯示、CSV 匯出、語法格式化、查詢歷史。 |
| 資料表資料編輯 | 可用 | 支援新增、修改、刪除與儲存；若沒有 Primary Key，預設更新/刪除前會顯示風險警告，也可在選項中改為唯讀開啟。 |
| Table Designer | 部分可用 | 支援新增資料表與多 provider ALTER 預覽/儲存；部分既有資料表修改與進階索引尚未完整支援。 |
| 自動補註解 | 可用 | 可從遠端字典補欄位註解，支援「補空白註解」與「覆蓋註解」兩種模式；SQLite 不支援欄位註解。 |
| 補註解進度視窗 | 可用 | 使用遮罩視窗與 CC0 貓咪跑者 GIF 顯示逐筆進度。 |
| 資料產生 | 可用 | Tables 群組可產生指定資料表的 INSERT SQL，可開到查詢視窗檢查，也可確認後逐筆直接寫入。 |
| 命令列介面 | 可用 | 支援 MySQL、PostgreSQL、SQL Server、SQLite、Oracle 的 CLI 啟動指令；需本機已安裝對應客戶端工具。 |
| Table/View 複製 | 可用 | 跨 provider 複製 Table/View；View SQL 無法安全轉換時會改用 table snapshot。 |
| SQL Dump | 可用 | 支援多 provider Table dump；各 provider 的 DDL 細節仍會依 metadata 能力不同而有差異。 |

## 未完成功能與已知限制

以下是從程式碼裡的「尚未支援」、「Unavailable」、「Unsupported」與實作 fallback 掃描出的清單。後續修改請優先參考這裡，把完成狀態同步更新。

### 優先待辦

- **群組功能 ✅ 已完成**
  - 觸發位置：左側樹狀清單與右鍵選單中的「新增群組 / 管理群組」。
  - 完成內容：
    - 連線支援 `conn_group` 欄位，儲存與讀取已整合至 `setting.ini`。
    - 左側樹狀清單以群組節點分類顯示連線。
    - 右鍵選單支援「移至群組」、「移出群組」、「重新命名群組」、「刪除群組」。
    - 語系文字已補齊。

- **多連線設定檔 ✅ 已完成**
  - 觸發位置：連線根節點右鍵選單的「切換連線設定檔」。
  - 完成內容：
    - 保留既有 `setting.ini` 作為預設設定檔，不破壞舊版連線資料。
    - 新增的連線設定檔會儲存在 `connection_profiles/*.json`，目前作用中的設定檔記錄於 `connection-profile.txt`。
    - 右鍵選單可查看目前設定檔、切換既有設定檔，或新增空白設定檔並立即切換。
    - 支援複製目前設定檔；非預設設定檔可重新命名或刪除，刪除目前設定檔後會切回預設設定檔。
    - 切換設定檔前會先儲存目前設定並關閉已開啟的連線，避免跨 profile 共用舊連線狀態。

### Provider 與資料庫操作限制

- **命令列介面依賴本機 CLI ✅ 偵測已補齊**
  - 現況：右鍵選單會依 provider 產生 `mysql`、`psql`、`sqlcmd`、`sqlite3` 或 `sqlplus` 指令並開啟命令提示字元。
  - 完成內容：
    - 已加入 CLI 可用性偵測（先透過 `where.exe`，再掃描 `PATH`）。找不到時會顯示安裝說明連結，不會直接開啟空白終端機。
    - `選項 > 環境` 可自訂 MySQL、PostgreSQL、SQL Server、Oracle、SQLite CLI 執行檔路徑；未設定時仍會使用 `PATH`，SQLite 會優先使用內建 `sqlite3.exe`。
  - 後續方向：更完整的密碼安全傳遞策略。

- **部分連線類型尚未支援編輯 ✅ 已完成**
  - 現況：MySQL、PostgreSQL、Oracle、SQLite、SQL Server 五種 provider 均有對應的編輯表單（template form）。
  - 完成內容：已修正編輯連線後 `conn_group` 欄位消失的問題——`update_connection` 現在會自動保留原連線的群組歸屬。
  - 後續方向：非上列 provider 的連線（未來擴充）仍會顯示「此連線類型尚未支援編輯」。

- **由主機節點新增/刪除資料庫只支援部分 provider ✅ 提示已補齊**
  - 現況：MySQL、PostgreSQL、SQL Server 支援從連線節點新增 / 刪除資料庫。
  - 完成內容：
    - Oracle：不支援直接 `CREATE DATABASE`（Oracle 使用 User/Schema 概念），操作時會顯示說明，提示改用 Oracle 管理工具或以 DBA 帳戶執行 `CREATE USER`。
    - SQLite：資料庫為獨立檔案，操作時會顯示說明，提示直接建立新的 SQLite 連線或刪除對應 `.sqlite` 檔案。
  - 後續方向：若有需求可在 Oracle 連線中實作 `CREATE USER` 精靈，但需要密碼與 Tablespace 等額外資訊。

- **SQLite 欄位註解不支援**
  - 現況：SQLite 本身沒有欄位註解語法，Table Designer 與補註解流程會擋下。
  - 後續方向：若需要 SQLite 註解，可另設 sidecar metadata table，但需先定義格式與匯出策略。

- **SpatiaLite extension 可能載入失敗 ✅ 診斷資訊已補齊**
  - 現況：SQLite provider 會嘗試載入 SpatiaLite；環境缺少 extension 時會顯示載入錯誤。`tools/spatialite/Build-SpatiaLiteRuntime.ps1` 可從官方原始碼重建 runtime，`mySQLPunk.csproj` 也會明確複製 `SQLite.Interop.dll` 的 x64/x86 runtime。
  - 完成內容：
    - 載入失敗訊息已改用語系化字串（`Connection.SpatiaLiteLoadFailed`），並同步更新狀態列，降級行為更清楚。
    - `其它 > 連線診斷` 會顯示 SpatiaLite runtime 目錄、`mod_spatialite.dll` 路徑、載入狀態與版本資訊。
  - 後續方向：若需要更完整的環境修復流程，可再加入一鍵重建 runtime 或下載指引。

### Table Designer 限制

- **既有資料表修改仍有不支援情境**
  - 現況：部分 ALTER TABLE 操作會列入「目前不支援以下既有資料表變更」。
  - 後續方向：以 provider 為單位補齊欄位改名、型別變更、NULL/DEFAULT、Primary Key 與 constraint 變更。

- **FULLTEXT / SPATIAL 索引只支援部分 provider 與語法**
  - 現況：不支援時會顯示「此資料庫尚未支援 FULLTEXT/SPATIAL 索引」。
  - 後續方向：逐 provider 補 MySQL FULLTEXT/SPATIAL、PostgreSQL GIN/GiST、SQL Server Full-Text、Oracle CTXSYS/MDSYS 等語法。

- **SQL Server DEFAULT constraint 變更仍有限制**
  - 現況：部分 DEFAULT constraint 需要先查 constraint name 再 drop/create。
  - 後續方向：建立完整 SQL Server column default 修改流程與測試。

- **Oracle Table Designer 對權限與物件狀態較敏感**
  - 現況：已有多種診斷提示，例如權限不足、物件不存在、跨 schema 權限、語法不符。
  - 後續方向：把 Oracle DDL 拆成更小步驟，並在預覽中標示可能需要的權限。

### 資料瀏覽與儲存限制

- **沒有 Primary Key 的資料表儲存風險較高 ✅ 唯讀選項已補齊**
  - 現況：儲存更新/刪除時，若沒有 Primary Key，會先顯示風險警告；繼續儲存時仍會用可用欄位建立 WHERE 條件。
  - 完成內容：`選項 > 一般` 新增「沒有 Primary Key 的資料表以唯讀模式開啟」。啟用後，開啟無 Primary Key 的資料表會自動停用新增、刪除與儲存操作，並在狀態列提示原因。
  - 風險：資料列被其他人改過，或欄位包含 BLOB/浮點/大文字時，WHERE 可能不穩定。
  - 後續方向：若仍需要編輯無 Primary Key 資料表，可再評估更細的 provider-specific optimistic locking。

- **BLOB/geometry 欄位操作仍有部分限制**
  - 現況：`byte[]` 欄位在結果表格中會先嘗試顯示為 `[Geometry] WKT`，無法解析時才顯示 `[BLOB n bytes] 0x...`；右鍵可檢視十六進位、複製 Hex、匯出檔案，在資料表資料模式可匯入檔案寫回目前 BLOB 欄位，也可針對 geometry 複製 WKT / WKT 轉 Geometry SQL。
  - 後續方向：補更完整的大型檔案串流檢視。

### Table/View 複製限制

- **View SQL 無法安全轉換時會改用 table snapshot ✅ 使用者選項與預覽已補齊**
  - 現況：跨 provider 複製 View 時，如果無法解析或轉換 SQL，會以查詢結果建立資料表快照。
  - 完成內容：複製前新增「跨 Provider 複製 View」對話框，讓使用者選擇：
    - **嘗試轉換 View SQL**（無法轉換時自動改為 table snapshot）
    - **直接建立 Table snapshot**（最穩定，不保留 View 語法）
    - 取消複製
    - 可在同一個對話框檢查來源 View SQL 與轉換後 SQL 預覽；若無法安全轉換，會顯示原因。
  - 已知情境：Oracle 階層查詢/ROWNUM、MySQL 專用 View 語法、無法解析的 SELECT SQL。
  - 後續方向：若需要更高相容性，可逐 provider 擴充更多 SQL 方言轉換規則。

## 專案檔案導覽

- `mySQLPunk/Program.cs`: 程式進入點。
- `mySQLPunk/Form1.cs`: 主視窗、左側連線樹、右鍵選單、metadata 瀏覽、資料庫級操作。
- `mySQLPunk/QueryForm.cs`: SQL 編輯器、查詢結果、資料表資料瀏覽與儲存。
- `mySQLPunk/TableDesignerForm.cs`: 資料表設計器、欄位/索引/SQL 預覽。
- `mySQLPunk/RunnerProgressOverlay.cs`: 補註解遮罩進度視窗。
- `mySQLPunk/AnimatedRunnerProgressBar.cs`: 跑者動畫進度條控制項。
- `mySQLPunk/AutoCommentMode.cs`: 補註解模式定義。
- `mySQLPunk/lib/IDatabase.cs`: database provider 介面。
- `mySQLPunk/lib/my_mysql.cs`: MySQL provider。
- `mySQLPunk/lib/my_postgresql.cs`: PostgreSQL provider。
- `mySQLPunk/lib/my_sqlite.cs`: SQLite provider。
- `mySQLPunk/lib/my_mssql.cs`: SQL Server provider。
- `mySQLPunk/lib/my_oracle.cs`: Oracle provider。
- `mySQLPunk/lib/DatabaseCopyService.cs`: Table/View 跨 provider 複製服務。
- `mySQLPunk/image/progress_runner.gif`: 補註解進度視窗的 CC0 貓咪跑者動畫。
- `mySQLPunk/image/progress_runner_LICENSE.txt`: 跑者動畫素材來源與授權資訊。

## 協作規範

- 多人共同維護時，開工前先執行 `git pull --ff-only origin master`。
- 修改完成後先建置或執行對應 smoke test，再 commit。
- Commit 後推送到遠端，避免本機進度落後。
- 修完 README 中的待辦或限制時，請同步更新本檔案狀態。
