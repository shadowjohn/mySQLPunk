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
| 連線管理 | 可用 | 連線資訊儲存在 `setting.ini`。 |
| MySQL | 可用 | 主要 provider，支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer。 |
| PostgreSQL | 可用 | 支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；部分進階索引仍有限制。 |
| SQLite | 可用 | 支援一般 SQLite 與 SpatiaLite 載入；SQLite 本身不支援欄位註解。 |
| SQL Server | 可用 | 支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；部分 DEFAULT constraint 與進階索引仍有限制。 |
| Oracle | 部分可用 | 支援 schema/table/view metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；部分 DDL 仍受權限、語法與物件型態限制。 |
| SQL 查詢 | 可用 | 支援 SELECT/SHOW/EXPLAIN/DESC/WITH 類結果顯示、CSV 匯出、語法格式化、查詢歷史。 |
| 資料表資料編輯 | 可用 | 支援新增、修改、刪除與儲存；若沒有 Primary Key，更新/刪除前會顯示風險警告。 |
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

- **群組功能尚未建立**
  - 觸發位置：左側樹狀清單與右鍵選單中的「新增群組 / 管理群組」。
  - 現況：點擊會顯示「尚未建立群組功能」。
  - 後續方向：定義群組資料結構、儲存位置、拖曳/搬移節點規則，以及語系文字。

- **多連線設定檔尚未支援**
  - 觸發位置：連線根節點右鍵選單的「切換連線設定檔」。
  - 現況：選單項目目前顯示為不可用。
  - 後續方向：設計 profile schema、UI 切換流程，以及既有 `setting.ini` 相容策略。

### Provider 與資料庫操作限制

- **命令列介面依賴本機 CLI**
  - 現況：右鍵選單會依 provider 產生 `mysql`、`psql`、`sqlcmd`、`sqlite3` 或 `sqlplus` 指令並開啟命令提示字元。
  - 後續方向：補上 CLI 可用性偵測、可自訂 CLI 路徑，以及更完整的密碼安全傳遞策略。

- **部分連線類型尚未支援編輯**
  - 現況：不支援的連線編輯會顯示「此連線類型尚未支援編輯」。
  - 後續方向：檢查每個 template form 是否具備讀寫所有連線欄位的能力。

- **由主機節點新增/刪除資料庫只支援部分 provider**
  - 現況：不支援時會顯示 provider 不支援新增或刪除資料庫。
  - 後續方向：補齊各 provider 的 `CREATE DATABASE` / `DROP DATABASE` SQL 與權限提示。

- **SQLite 欄位註解不支援**
  - 現況：SQLite 本身沒有欄位註解語法，Table Designer 與補註解流程會擋下。
  - 後續方向：若需要 SQLite 註解，可另設 sidecar metadata table，但需先定義格式與匯出策略。

- **SpatiaLite extension 可能載入失敗**
  - 現況：SQLite provider 會嘗試載入 SpatiaLite；環境缺少 extension 時會顯示載入錯誤。`tools/spatialite/Build-SpatiaLiteRuntime.ps1` 可從官方原始碼重建 runtime，`mySQLPunk.csproj` 也會明確複製 `SQLite.Interop.dll` 的 x64/x86 runtime。
  - 後續方向：補齊 UI 診斷與無 SpatiaLite 時的降級行為。

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

- **沒有 Primary Key 的資料表儲存風險較高**
  - 現況：儲存更新/刪除時，若沒有 Primary Key，會先顯示風險警告；繼續儲存時仍會用可用欄位建立 WHERE 條件。
  - 風險：資料列被其他人改過，或欄位包含 BLOB/浮點/大文字時，WHERE 可能不穩定。
  - 後續方向：視使用者回饋決定是否提供「沒有 Primary Key 時唯讀」的選項。

- **BLOB/geometry 欄位操作仍有部分限制**
  - 現況：`byte[]` 欄位在結果表格中會先嘗試顯示為 `[Geometry] WKT`，無法解析時才顯示 `[BLOB n bytes] 0x...`；右鍵可檢視十六進位、複製 Hex、匯出檔案，在資料表資料模式可匯入檔案寫回目前 BLOB 欄位，也可針對 geometry 複製 WKT / WKT 轉 Geometry SQL。
  - 後續方向：補更完整的大型檔案串流檢視。

### Table/View 複製限制

- **View SQL 無法安全轉換時會改用 table snapshot**
  - 現況：跨 provider 複製 View 時，如果無法解析或轉換 SQL，會以查詢結果建立資料表快照。
  - 已知情境：Oracle 階層查詢/ROWNUM、MySQL 專用 View 語法、無法解析的 SELECT SQL。
  - 後續方向：新增轉換預覽與使用者選項，讓使用者決定要保留 View、改成 Table snapshot，或取消。

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
- Commit message 使用繁體中文，並遵守 Conventional Commits：
  - 例：`feat(comment): 新增補註解模式選擇`
  - body 建議包含「原因 / 調整 / 影響」。
- Commit 後推送到遠端，避免本機進度落後。
- 修完 README 中的待辦或限制時，請同步更新本檔案狀態。
