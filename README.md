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
| PostgreSQL | 可用 | 支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；`public` 以外的 schema 會以 `schema.table` 顯示並可用於主要資料表操作，部分進階索引仍有限制。 |
| SQLite | 可用 | 支援一般 SQLite 與 SpatiaLite 載入；欄位註解以 mySQLPunk sidecar metadata table 保存。 |
| SQL Server | 可用 | 支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；`dbo` 以外的 schema 會以 `schema.table` 顯示並可用於主要資料表操作。 |
| Oracle | 部分可用 | 支援 schema/table/view metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；部分 DDL 仍受權限、語法與物件型態限制。 |
| SQL 查詢 | 可用 | 支援 SELECT/SHOW/EXPLAIN/DESC/WITH 類結果顯示、多格式匯出、語法格式化、查詢歷史。 |
| 資料表資料編輯 | 可用 | 支援新增、修改、刪除與儲存；若沒有 Primary Key，預設更新/刪除前會顯示風險警告，也可在選項中改為唯讀開啟。 |
| Table Designer | 部分可用 | 支援新增資料表與多 provider ALTER 預覽/儲存；部分既有資料表修改與進階索引尚未完整支援。 |
| 自動補註解 | 可用 | 可從遠端字典補欄位註解，支援「補空白註解」與「覆蓋註解」兩種模式；SQLite 會寫入 sidecar metadata table。 |
| 補註解進度視窗 | 可用 | 使用遮罩視窗與 CC0 貓咪跑者 GIF 顯示逐筆進度。 |
| 資料產生 | 可用 | Tables 節點可產生指定資料表的 INSERT SQL，可開到查詢視窗檢查，也可確認後逐筆直接寫入。 |
| 命令列介面 | 可用 | 支援 MySQL、PostgreSQL、SQL Server、SQLite、Oracle 的 CLI 啟動指令；需本機已安裝對應客戶端工具。 |
| Table/View 複製 | 可用 | 跨 provider 複製 Table/View；View SQL 無法安全轉換時會改用 table snapshot。 |
| SQL Dump | 可用 | 支援多 provider Table dump；SQLite 欄位註解 sidecar metadata 會隨結構匯出；各 provider 的 DDL 細節仍會依 metadata 能力不同而有差異。 |

## 未完成功能與已知限制

以下是從程式碼裡的「尚未支援」、「Unavailable」、「Unsupported」與實作 fallback 掃描出的清單。後續修改請優先參考這裡，把完成狀態同步更新。

### 優先待辦

- **連線群組功能 ✅ 已完成**
  - 觸發位置：左側樹狀清單空白處右鍵選單的「新增群組」，以及連線/群組節點右鍵選單的群組操作項目。
  - 完成內容：
    - 連線支援 `conn_group` 欄位，儲存與讀取已整合至 `setting.ini`。
    - 左側樹狀清單以群組節點分類顯示連線。
    - 右鍵選單支援「移至群組」、「移出群組」、「重新命名群組」、「刪除群組」。
    - 語系文字已補齊。
  - 備註：資料庫物件分類（Tables / Views / Queries）目前不支援群組分類。

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
    - 已補上密碼傳遞策略：MySQL、PostgreSQL、SQL Server 會透過 `MYSQL_PWD`、`PGPASSWORD`、`SQLCMDPASSWORD` 環境變數傳給 CLI，不把密碼寫進命令列參數；未儲存密碼時仍會保留 CLI 互動式密碼提示。Oracle `sqlplus` 目前仍採互動式密碼輸入，避免把密碼放進連線字串。
    - 未儲存密碼時，MySQL、PostgreSQL、SQL Server 會在開啟 CLI 前顯示一次性密碼輸入框；輸入的密碼只放入本次 process environment，不會回寫到連線設定或設定檔。
  - 後續方向：若要進一步強化 CLI 密碼保存，可評估 Windows Credential Manager。

- **部分連線類型尚未支援編輯 ✅ 已完成**
  - 現況：MySQL、PostgreSQL、Oracle、SQLite、SQL Server 五種 provider 均有對應的編輯表單（template form）。
  - 完成內容：已修正編輯連線後 `conn_group` 欄位消失的問題——`update_connection` 現在會自動保留原連線的群組歸屬。
  - 後續方向：非上列 provider 的連線（未來擴充）仍會顯示「此連線類型尚未支援編輯」。

- **由主機節點新增/刪除資料庫只支援部分 provider ✅ Oracle Schema 精靈已補齊**
  - 現況：MySQL、PostgreSQL、SQL Server 支援從連線節點新增 / 刪除資料庫。
  - 完成內容：
    - Oracle：新增資料庫會開啟 Oracle Schema 精靈，輸入使用者、密碼、預設/暫存 Tablespace 後依序執行 `CREATE USER`、`ALTER USER ... QUOTA` 與常用物件建立權限 grant；需使用具備建立 user 權限的帳戶。
    - Oracle：刪除資料庫會使用 `DROP USER ... CASCADE`，執行前會先顯示高風險提示並要求再次輸入完整 Schema 名稱；SYS、SYSTEM、XDB、CTXSYS、MDSYS 等系統 Schema 會被阻擋。
    - SQLite：資料庫為獨立檔案；刪除時會解析連線檔案路徑、確認檔案存在且不是目錄，並要求再次輸入完整檔名後才會先建立刪除前備份，再關閉連線、清除 SQLite connection pool，並將 `.sqlite`、`-wal`、`-shm`、`-journal` 檔案移到資源回收筒；若檔案系統不支援資源回收筒，才會 fallback 成直接刪除。
  - 後續方向：若需要更完整的資料保護，可再加入多版本備份保留策略或刪除前壓縮封存。

- **SQLite 欄位註解不支援 ✅ sidecar metadata 已補齊**
  - 現況：SQLite 本身沒有欄位註解語法，因此 mySQLPunk 會使用 `__mysqlpunk_column_comments` sidecar metadata table 保存欄位註解。
  - 完成內容：SQLite provider 讀取欄位時會合併 sidecar 註解；Table Designer 新增/修改/重建資料表與資料庫/資料表補註解流程都會寫入 sidecar metadata。
  - 匯出支援：SQL Dump 的結構匯出會附帶 sidecar table 建立語句與目前資料表的欄位註解 `INSERT OR REPLACE`，跨環境還原後可保留 mySQLPunk 欄位註解。
  - 後續方向：若需要和其它 SQLite 工具交換欄位註解，可再定義專用匯出格式或轉換器。

- **SpatiaLite extension 可能載入失敗 ✅ 診斷資訊已補齊**
  - 現況：SQLite provider 會嘗試載入 SpatiaLite；環境缺少 extension 時會顯示載入錯誤。`tools/spatialite/Build-SpatiaLiteRuntime.ps1` 可從官方原始碼重建 runtime，`mySQLPunk.csproj` 也會明確複製 `SQLite.Interop.dll` 的 x64/x86 runtime。
  - 完成內容：
    - 載入失敗訊息已改用語系化字串（`Connection.SpatiaLiteLoadFailed`），並同步更新狀態列，降級行為更清楚。
    - `其它 > 連線診斷` 會顯示 SpatiaLite runtime 目錄、`mod_spatialite.dll` 路徑、載入狀態與版本資訊。
  - 後續方向：若需要更完整的環境修復流程，可再加入一鍵重建 runtime 或下載指引。

### Table Designer 限制

- **自動補註解字典為遠端服務，可能受網路影響 ✅ 本機快取、匯入匯出、版本資訊、差異預覽、來源提示與多份字典切換已補齊**
  - 現況：Table Designer 的「補註解」會載入遠端字典對照表；成功載入後會保存到本機快取。
  - 完成內容：若網路/站台/SSL 等因素導致遠端載入失敗，會在重試後改用上次成功的本機快取，避免補註解功能完全不可用；補註解進度視窗會標示目前使用的是遠端字典、本機快取、匯入字典或已命名字典；Table Designer 的補註解下拉選單可手動匯入 / 匯出 JSON 字典檔，方便離線環境或團隊共用欄位註解對照；匯出的字典會包含 `version`、`exportedAtUtc`、`source` 與 `entryCount`，匯入仍相容舊版純 key/value JSON；匯入前會顯示新增、更新、移除與不變項目的差異預覽，確認後才覆蓋本機字典；也可以將目前字典另存為命名字典，之後直接從下拉選單切換使用。
  - 後續方向：若需要更完整的字典管理，可再加入命名字典刪除、重新命名與逐項差異檢視。

- **既有資料表修改仍有不支援情境**
  - 現況：部分 ALTER TABLE 操作會列入「目前不支援以下既有資料表變更」；PostgreSQL Table Designer 已支援 `schema.table` 形式的既有資料表 SQL 產生，不再固定套用 `public` schema。
  - 本輪補齊：PostgreSQL provider 會列出非 `public` schema 的 Table/View、Function 與 Trigger，並讓欄位、索引、資料瀏覽、列數、複製建表、View DDL 與批次寫入等主要操作依 `schema.table` 產生正確 SQL；QueryForm 資料表新增/更新/刪除與 Form1 共用物件 SQL（開啟查詢、Drop、Dump/DDL、資料產生、補註解）也會依 `schema.table` 寫入正確 schema；Table Designer 欄位修改、註解、Primary Key 變更與索引刪除的 SQL 預覽也會依目前資料表 schema 產生正確物件名稱；新增 View / Function 範本會沿用目前選取物件的 schema，避免在非預設 schema 工作時又產生 `public` / `dbo` 範本。
  - 後續方向：以 provider 為單位補齊欄位改名、型別變更、NULL/DEFAULT、Primary Key 與 constraint 變更。

- **FULLTEXT / SPATIAL 索引只支援部分 provider 與語法 ✅ 主要 provider 已補齊**
  - 現況：Table Designer 支援 MySQL FULLTEXT/SPATIAL、PostgreSQL FULLTEXT GIN 與 SPATIAL GiST、SQL Server Full-Text / Spatial、Oracle CTXSYS/MDSYS 索引 SQL 產生；SQLite 仍不提供一般 FULLTEXT/SPATIAL 索引設計器入口。
  - 完成內容：新增與修改資料表流程都會依 provider 產生對應 FULLTEXT/SPATIAL 語法；MySQL 既有資料表 ALTER 已補上 `ADD SPATIAL INDEX`，索引註解也會套用 MySQL 字串 escape。
  - 後續方向：若要支援 SQLite FTS virtual table、RTree 或 SpatiaLite spatial index，需要另做專用精靈，避免用一般索引 UI 產生錯誤語法。

- **SQL Server DEFAULT constraint 變更仍有限制 ✅ 已補齊**
  - 現況：SQL Server 會把欄位 DEFAULT 存成 default constraint，修改時不能只用一般 `ALTER COLUMN` 覆蓋。
  - 完成內容：Table Designer 修改欄位 DEFAULT 時會先查 `sys.default_constraints` 找到實際 constraint name 後 drop，再以 `DF_<table>_<column>` 規則建立具名 DEFAULT constraint；SQL Server 預覽 SQL 分段執行時也會保留 `DECLARE` batch，避免變數 scope 被切壞。
  - Schema 支援：SQL Server provider 會列出所有 schema 的 Table/View；`dbo` 維持原表名顯示，非 `dbo` 會顯示為 `schema.table`。資料瀏覽、資料編輯、DDL/Dump、Table/View 複製、補註解與 Table Designer SQL 產生都會解析 schema，不再硬套 `dbo`。

- **Oracle Table Designer 對權限與物件狀態較敏感 ✅ 預覽提示已補齊**
  - 現況：已有多種診斷提示，例如權限不足、物件不存在、跨 schema 權限、語法不符。
  - 完成內容：Oracle SQL 預覽會在可執行 SQL 前加入註解提示，標示目標物件、直接授權需求（ALTER / CREATE INDEX / DROP / COMMENT 等）與分段執行方式；儲存判斷也改成檢查是否存在真正可執行 SQL，避免預覽註解誤擋操作。
  - 後續方向：若需要更完整的 Oracle 風險控管，可再加入實際權限偵測查詢與執行前逐步確認。

### 資料瀏覽與儲存限制

- **沒有 Primary Key 的資料表儲存風險較高 ✅ 唯讀選項已補齊**
  - 現況：儲存更新/刪除時，若沒有 Primary Key，會先顯示風險警告；繼續儲存時仍會用可用欄位建立 WHERE 條件。
  - 完成內容：`選項 > 一般` 新增「沒有 Primary Key 的資料表以唯讀模式開啟」。啟用後，開啟無 Primary Key 的資料表會自動停用新增、刪除與儲存操作，並在狀態列提示原因。
  - 風險：資料列被其他人改過，或欄位包含 BLOB/浮點/大文字時，WHERE 可能不穩定。
  - 後續方向：若仍需要編輯無 Primary Key 資料表，可再評估更細的 provider-specific optimistic locking。

- **BLOB/geometry 欄位操作仍有部分限制 ✅ 分頁檢視已補齊**
  - 現況：`byte[]` 欄位在結果表格中會先嘗試顯示為 `[Geometry] WKT`，無法解析時才顯示 `[BLOB n bytes] 0x...`；右鍵可檢視十六進位、複製 Hex、匯出檔案，在資料表資料模式可匯入檔案寫回目前 BLOB 欄位，也可針對 geometry 複製 WKT / WKT 轉 Geometry SQL。
  - 完成內容：BLOB 十六進位檢視器改為 4KB 分頁顯示，支援首頁、上一頁、下一頁、末頁與複製本頁 Hex，避免大型 BLOB 一次轉成完整文字造成 UI 卡頓。
  - 後續方向：若需要直接處理超大型欄位，可再評估 provider-level streaming 讀取，避免結果集本身先載入完整 `byte[]`。

- **查詢結果匯出格式 ✅ 已補齊常用格式**
  - 現況：查詢結果匯出預設使用 CSV，並可在儲存對話框選擇 Excel `.xlsx`、TSV、JSON、XML、HTML 或 Markdown。
  - 完成內容：各格式會共用結果表格顯示值轉換；BLOB/geometry 會沿用結果表格的 `[Geometry] WKT` 或 `[BLOB n bytes]` 顯示，日期與空值也會一致處理。
  - 後續方向：若需要直接匯出大型結果集，可再評估 provider-level streaming export，避免整份 DataTable 先載入記憶體。

### Table/View 複製限制

- **View SQL 無法安全轉換時會改用 table snapshot ✅ 使用者選項與預覽已補齊**
  - 現況：跨 provider 複製 View 時，如果無法解析或轉換 SQL，會以查詢結果建立資料表快照。
  - 完成內容：複製前新增「跨 Provider 複製 View」對話框，讓使用者選擇：
    - **嘗試轉換 View SQL**（無法轉換時自動改為 table snapshot）
    - **直接建立 Table snapshot**（最穩定，不保留 View 語法）
    - 取消複製
    - 可在同一個對話框檢查來源 View SQL 與轉換後 SQL 預覽；若無法安全轉換，會顯示原因。
  - 方言轉換：已支援 SQL Server `TOP (n)` 轉 MySQL/PostgreSQL/SQLite `LIMIT n` 或 Oracle `FETCH FIRST`、MySQL/PostgreSQL/SQLite `LIMIT` 轉 SQL Server `TOP (n)` 或 Oracle `FETCH/OFFSET`、簡單 Oracle `ROWNUM <= n` 轉目標 provider row limit，以及 `NVL` / `IFNULL` / `ISNULL` / `GETDATE()` / `NOW()` 的常見函式轉換。
  - 進階轉換：已補上簡單 `DATE_FORMAT`、`GROUP_CONCAT` / `group_concat` / `STRING_AGG` / `LISTAGG`、`JSON_EXTRACT` 的跨 provider 轉換，讓常見日期格式、字串聚合與 JSON 純量讀取可以保留 View SQL。
  - 已知情境：Oracle 階層查詢、MySQL 專用 View 語法、帶 OFFSET 且缺少穩定排序的 SQL Server 轉換、無法解析的 SELECT SQL 仍會改用 table snapshot。
  - 後續方向：若需要更高相容性，可再逐 provider 擴充 window function、CTE 遞迴、JSON table 與 provider 專用內建函式等更複雜 SQL 方言轉換規則。

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

- `history.md` 是本機筆記且已被 `.gitignore` 忽略；正式狀態以 git commit 與本 README 為準。
- 每次新增或修改一個明確功能，先同步遠端、再修改、測試成功後 commit，最後 push 到 `origin/master` 上版。
- 標準流程：

```powershell
git status --short --branch
git fetch origin
git pull --rebase origin master

# 修改功能 / 修 bug

dotnet msbuild .\mySQLPunk.sln /p:Configuration=Debug /p:Platform="Any CPU" /v:minimal /nologo

git status --short
git add <本次相關檔案>
git diff --cached
git commit -m "type(scope): 繁中說明"
git push origin master
```

- 建置失敗不 commit、不 push；有 UI/DB 行為變更時，除了建置，還要做對應 smoke test。
- 若 `git pull --rebase origin master` 發生衝突，先停下來處理衝突並重新測試，再進 commit/push 流程。
- Commit 前必須執行 `git diff --cached` 確認 staged 內容只包含本次相關修改，避免簽入非預期檔案。
- 非必要禁止 `git push --force` 或 `git push --force-with-lease`；若真的需要改寫遠端歷史，先明確確認原因、影響範圍與回復方式。
- Commit message 延續現有格式：`feat(scope): ...`、`fix(scope): ...`、`docs(scope): ...`、`style(scope): ...`、`refactor(scope): ...`。
- 常用 scope：`query`、`designer`、`sqlite`、`sqlserver`、`oracle`、`copy`、`cli`、`connection`、`tree`、`export`、`ui`。
- 修完 README 中的待辦或限制時，請同步更新本檔案狀態。
