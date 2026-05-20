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

Smoke test harness：

```powershell
.\tests\Run-SmokeTests.ps1
```

目前 smoke test 會先建置 `mySQLPunk.sln`，再編譯並執行 `tests/SmokeTests.cs`，覆蓋 `DatabaseCopyService` 的 View SQL 跨 provider 轉換（TOP / LIMIT / ROWNUM、日期、字串聚合、JSON、CTE/window 與 unsupported reason）、`GeometryWktConverter` 的 WKB/WKT 基本轉換與錯誤案例、SQLite FTS/RTree/SpatiaLite 專用 SQL builder、Table Designer 主要 DDL builder 的 MySQL / SQLite 建表與 MySQL / PostgreSQL / SQL Server / Oracle / SQLite 既有資料表 ALTER 輸出，以及 `DatabaseDumpService` / `QueryResultExportService` / `ConnectionOpenService` / `MetadataLoadService` 的非 UI service 測試。

## 目前功能概況

| 功能 | 狀態 | 說明 |
| --- | --- | --- |
| 連線管理 | 可用 | 預設連線資訊儲存在 `setting.ini`，並支援切換多個連線設定檔；密碼改存 Windows Credential Manager，設定檔只保留 credential target。 |
| MySQL | 可用 | 主要 provider，支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer。 |
| PostgreSQL | 可用 | 支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；`public` 以外的 schema 會以 `schema.table` 顯示並可用於主要資料表操作，部分進階索引仍有限制。 |
| SQLite | 可用 | 支援一般 SQLite 與 SpatiaLite 載入；欄位註解以 mySQLPunk sidecar metadata table 保存。 |
| SQL Server | 可用 | 支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；`dbo` 以外的 schema 會以 `schema.table` 顯示並可用於主要資料表操作。 |
| Oracle | 部分可用 | 支援 schema/table/view metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；部分 DDL 仍受權限、語法與物件型態限制。 |
| SQL 查詢 | 可用 | 支援 SELECT/SHOW/EXPLAIN/DESC/WITH 類結果顯示、多格式匯出、語法格式化、查詢歷史。 |
| 資料表資料編輯 | 可用 | 支援新增、修改、刪除與儲存；若沒有 Primary Key，預設更新/刪除前會顯示風險警告，也可在選項中改為唯讀開啟。 |
| Table Designer | 部分可用 | 支援新增資料表與多 provider ALTER 預覽/儲存；既有資料表欄位改名、型別、NULL、DEFAULT、註解、MySQL 刪欄位與 SQLite 重建表已納入 smoke test，部分進階索引與 constraint 情境仍需實機驗證。 |
| 自動補註解 | 可用 | 可從遠端字典補欄位註解，支援「補空白註解」與「覆蓋註解」兩種模式；SQLite 會寫入 sidecar metadata table。 |
| 補註解進度視窗 | 可用 | 使用遮罩視窗與 CC0 貓咪跑者 GIF 顯示逐筆進度。 |
| 資料產生 | 可用 | Tables 節點可產生指定資料表的 INSERT SQL，可開到查詢視窗檢查，也可確認後逐筆直接寫入。 |
| 命令列介面 | 可用 | 支援 MySQL、PostgreSQL、SQL Server、SQLite、Oracle 的 CLI 啟動指令；需本機已安裝對應客戶端工具。 |
| Table/View 複製 | 可用 | 跨 provider 複製 Table/View；View SQL 無法安全轉換時會改用 table snapshot。 |
| SQL Dump | 可用 | 支援多 provider Table dump；SQLite 欄位註解 sidecar metadata 會隨結構匯出；各 provider 的 DDL 細節仍會依 metadata 能力不同而有差異。 |
| 匯出 / Dump / Backup service | 可用 | 查詢結果多格式匯出、SQL dump 與邏輯 SQL 備份已抽出 service，Form UI 只負責觸發、檔案對話框與狀態呈現。 |
| 連線與 metadata service | 可用 | 連線開啟、retry 判斷與 database metadata snapshot 已抽出 service，Form UI 保留 TreeView 呈現與錯誤提示。 |
| 選項中心 | 部分可用 | 已補齊主要分類頁與 `application-options.json` 保存；查詢視窗已套用記錄限制、編輯器字型/換行/Tab 空格、自動完成開關、SQL 檔案位置、匯出位置、結果網格字型、診斷記錄、自動復原草稿、索引標籤開啟偏好、HTTP 代理與進階註冊設定。 |

## 未完成功能與已知限制

以下是從程式碼裡的「尚未支援」、「Unavailable」、「Unsupported」與實作 fallback 掃描出的清單。後續修改請優先參考這裡，把完成狀態同步更新。

### 優先待辦

- **選項中心分類功能 ✅ 查詢視窗、診斷記錄、自動復原草稿、索引標籤偏好、HTTP 代理、檢視顯示偏好與進階註冊已補齊**
  - 現況：選項視窗已具備一般、索引標籤、自動完成程式碼、編輯器、記錄、AI、自動復原、檔案位置、連線能力、環境與進階等分類。
  - 完成內容：`ApplicationOptionSettings` 會將通用選項保存到 `application-options.json`；查詢視窗已讀取並套用記錄限制、編輯器字型大小、換行、自動完成啟用狀態、是否自動載入 metadata、匯出預設資料夾、SQL 開啟/儲存資料夾與結果網格字型大小；進階選項的「啟用診斷記錄」會把查詢歷程以 JSONL 寫入 `選項 > 檔案位置` 的記錄位置，內容保留 SQL 預覽與 SHA-256 指紋，不保存完整 SQL 原文；自動復原的查詢開關與間隔會啟動查詢草稿定時保存，草稿寫入查詢資料夾下的 `auto-recovery`；索引標籤設定可決定新查詢開在主視窗分頁、最後使用位置或新視窗，且「允許重複開啟相同的物件」關閉時會重用同名同型別分頁；檢視選單的導覽窗格（含僅顯示活躍物件）、資訊窗格、清單/詳細資料、欄位排序（含遞增/遞減）、欄位顯示、頂部即時篩選與「顯示隱藏的項目」會保存設定，其中僅顯示活躍物件會隱藏空的物件分類，隱藏項目會預設過濾 SQLite 系統表、SpatiaLite metadata、sidecar metadata 與常見 provider 系統物件；連線能力的 HTTP 代理設定會套用到 WebRequest/WebClient 路徑，例如自動補註解字典下載與共用 HTTP helper；進階註冊可在目前使用者層級註冊 SQL 檔案開啟方式與 `mysqlpunk://` URL 協定，關閉選項時只移除本程式建立的註冊項目。
  - 後續方向：部分選項仍需逐步接到實際行為，例如 SOCKS5/provider 原生資料庫連線代理。

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

- **命令列介面依賴本機 CLI ✅ 偵測、匯入預覽與匯入後補密碼已補齊**
  - 現況：右鍵選單會依 provider 產生 `mysql`、`psql`、`sqlcmd`、`sqlite3` 或 `sqlplus` 指令並開啟命令提示字元。
  - 完成內容：
    - 已加入 CLI 可用性偵測（先透過 `where.exe`，再掃描 `PATH`）。找不到時會顯示安裝說明連結，不會直接開啟空白終端機。
    - `選項 > 環境` 可自訂 MySQL、PostgreSQL、SQL Server、Oracle、SQLite CLI 執行檔路徑；未設定時仍會使用 `PATH`，SQLite 會優先使用內建 `sqlite3.exe`。
    - 已補上密碼傳遞策略：MySQL、PostgreSQL、SQL Server 會透過 `MYSQL_PWD`、`PGPASSWORD`、`SQLCMDPASSWORD` 環境變數傳給 CLI，不把密碼寫進命令列參數；未儲存密碼時仍會保留 CLI 互動式密碼提示。Oracle `sqlplus` 目前仍採互動式密碼輸入，避免把密碼放進連線字串。
    - 連線密碼會儲存在 Windows Credential Manager；舊版 `setting.ini` / profile JSON 中的加密 `pwd` 會在讀取時自動遷移到 credential，並重寫設定檔清空 `pwd`。編輯連線時若清空密碼欄位，會同步清除對應 credential。
    - 未儲存密碼時，MySQL、PostgreSQL、SQL Server 會在開啟 CLI 前顯示一次性密碼輸入框；輸入的密碼只放入本次 process environment，不會回寫到連線設定或設定檔。
    - 匯入連線設定前會顯示差異預覽，依新增、更新、不變與只存在目前設定分類；使用者可選擇整包取代，也可只合併勾選的匯入連線，保留未勾選與本機既有連線；匯出的連線 JSON 會附帶來源 ID 與 SHA-256 來源簽章，匯入預覽會顯示簽章是否有效，並可將已確認的匯出來源加入本機信任來源白名單，方便辨識檔案是否被修改與是否來自已信任來源。
    - 匯入連線設定後，若偵測到 MySQL、PostgreSQL、Oracle 或非 Windows 驗證的 SQL Server 連線缺少密碼，會詢問是否開啟批次補密碼視窗；使用者可一次補多筆密碼，密碼只會寫入 Windows Credential Manager，不會出現在匯入檔或設定檔明文中。
  - 後續方向：若要進一步強化跨機匯入，可再加入團隊共享設定審核流程，讓多位維護者可集中審核匯出來源。

- **部分連線類型尚未支援編輯 ✅ 已完成**
  - 現況：MySQL、PostgreSQL、Oracle、SQLite、SQL Server 五種 provider 均有對應的編輯表單（template form）。
  - 完成內容：已修正編輯連線後 `conn_group` 欄位消失的問題——`update_connection` 現在會自動保留原連線的群組歸屬。
  - 後續方向：非上列 provider 的連線（未來擴充）仍會顯示「此連線類型尚未支援編輯」。

- **由主機節點新增/刪除資料庫只支援部分 provider ✅ 高風險刪除備份、壓縮封存、還原入口與保留上限已補齊**
  - 現況：MySQL、PostgreSQL、SQL Server 支援從連線節點新增 / 刪除資料庫。
  - 完成內容：
    - Oracle：新增資料庫會開啟 Oracle Schema 精靈，輸入使用者、密碼、預設/暫存 Tablespace 後依序執行 `CREATE USER`、`ALTER USER ... QUOTA` 與常用物件建立權限 grant；需使用具備建立 user 權限的帳戶。
    - Oracle：刪除資料庫會使用 `DROP USER ... CASCADE`，執行前會先顯示高風險提示並要求再次輸入完整 Schema 名稱；SYS、SYSTEM、XDB、CTXSYS、MDSYS 等系統 Schema 會被阻擋。
    - SQLite：資料庫為獨立檔案；刪除時會解析連線檔案路徑、確認檔案存在且不是目錄，並要求再次輸入完整檔名後才會先建立刪除前備份，再關閉連線、清除 SQLite connection pool，並將 `.sqlite`、`-wal`、`-shm`、`-journal` 檔案移到資源回收筒；若檔案系統不支援資源回收筒，才會 fallback 成直接刪除。
    - MySQL / PostgreSQL / SQL Server / Oracle：刪除前會詢問是否先建立邏輯 SQL 備份，預設輸出到文件目錄的 `mySQLPunk/pre-delete-backups`；選擇備份時若建立失敗，刪除會中止，不會直接執行 `DROP DATABASE` 或 `DROP USER ... CASCADE`。
    - 刪除前備份建立成功後會自動封存為 `.zip`，zip 內保留原始 `.sql` 或 SQLite 備份檔名；封存成功後會移除未壓縮暫存檔，並在同目錄保留最近 20 份 `*_before_delete_*.zip`，降低備份目錄長期膨脹風險。
    - 還原支援：資料庫與 Backups 節點右鍵可開啟「還原備份」，支援 `.sql` 與含 `.sql` 的 `.zip`；還原前會顯示 SQL 語句數、大小與目標資料庫，確認後會先建立還原前快照並壓縮保存到 `mySQLPunk/pre-restore-backups`，快照成功後才沿用 SQL 匯入執行器套用；還原完成後會比較還原前後的資料表、檢視、函式/程序與事件/Trigger 數量，並列出新增/移除的物件名稱，讓使用者立即看到物件摘要變化。
    - 遠端副本支援：`選項 > 檔案位置` 可設定遠端備份資料夾與保留份數；每次建立備份成功後會自動複製一份到該資料夾，適合 NAS、雲端同步資料夾或團隊共享磁碟；若同名檔案已存在，會自動加上流水號避免覆蓋，並只清理 mySQLPunk 產生樣式的舊備份檔，避免誤刪同資料夾其它檔案。
    - 完整性驗證：備份建立後會立即驗證本機檔案；SQL 備份需有可執行語句，ZIP 備份需包含可讀 SQL 或 SQLite 備份，SQLite 備份會執行 `PRAGMA integrity_check`；若有遠端副本，也會在複製後驗證副本，驗證失敗會回報錯誤。
    - 定期排程：`選項 > 檔案位置` 可開關「定期驗證備份完整性」並設定驗證間隔；主程式啟動後會在背景檢查本機刪除前備份、還原前快照與遠端副本資料夾，驗證結果只顯示在狀態列，不跳出干擾式通知；每次排程也會輸出 JSON 報表到文件目錄的 `mySQLPunk/backup-integrity-reports`，方便追查異常備份。
    - 異常隔離：`選項 > 檔案位置` 可選擇驗證失敗時自動隔離異常備份，並設定隔離區保留份數；開啟後排程會把失敗檔案移到文件目錄的 `mySQLPunk/backup-quarantine`，輸出隔離 manifest，並只清理 mySQLPunk 產生樣式的舊隔離檔，預設關閉以避免未經同意移動備份。
    - 隔離區還原入口：資料庫與 Backups 節點右鍵可開啟「還原隔離備份」，從 `backup-quarantine` 選擇被隔離的備份檔，若 manifest 保留原始路徑會預填還原位置；移回前會顯示隔離檔路徑、原始路徑、大小、二次完整性驗證結果與目標檔案差異預覽（原始路徑是否存在、大小變化與是否需覆蓋確認），還原動作只把備份檔移回指定位置，不會直接灌入資料庫，使用者可先檢查檔案後再走既有「還原備份」流程；也可使用「批次還原隔離備份」把有原始路徑的隔離檔移回原處，目標檔已存在時會略過不覆蓋。
  - 後續方向：若需要更完整的資料保護，可再加入資料列層級或 schema 欄位層級的還原前後差異比較。

- **SQLite 欄位註解不支援 ✅ sidecar metadata 與交換格式已補齊**
  - 現況：SQLite 本身沒有欄位註解語法，因此 mySQLPunk 會使用 `__mysqlpunk_column_comments` sidecar metadata table 保存欄位註解。
  - 完成內容：SQLite provider 讀取欄位時會合併 sidecar 註解；Table Designer 新增/修改/重建資料表與資料庫/資料表補註解流程都會寫入 sidecar metadata。
  - 匯出支援：SQL Dump 的結構匯出會附帶 sidecar table 建立語句與目前資料表的欄位註解 `INSERT OR REPLACE`，跨環境還原後可保留 mySQLPunk 欄位註解；SQLite 資料庫、Tables 節點與單一資料表右鍵可匯出 / 匯入專用欄位註解 JSON、XLSX、CSV、YAML，匯入時會建立 sidecar table 並覆蓋匯入範圍內資料表的註解；XLSX 匯出模板會包含 `provider`、`database`、`table`、`column`、`type`、`not_null`、`default_value`、`comment` 欄位，方便人工審核或交給外部工具補註解；CLI 可用 `--sqlite-comments-export --database <sqlite> --output <json|xlsx|csv|yaml> [--table <name>]` 與 `--sqlite-comments-import --database <sqlite> --input <json|xlsx|csv|yaml> [--table <name>]` 進行自動化交換；匯入也支援第三方扁平陣列 JSON（`table` / `column` / `comment`）、table 內 columns 陣列格式、`table,column,comment` CSV、含額外輔助欄位的 XLSX 工作表與簡化 YAML comments 清單，並接受 `object_name` / `field_name` / `comment_text`、`entity` / `attribute` / `note` 等常見第三方模板別名，方便外部工具轉接。
  - 後續方向：若需要和其它 SQLite 工具深度整合，可再加入審核流程。

- **SpatiaLite extension 可能載入失敗 ✅ 診斷資訊已補齊**
  - 現況：SQLite provider 會嘗試載入 SpatiaLite；環境缺少 extension 時會顯示載入錯誤。`tools/spatialite/Build-SpatiaLiteRuntime.ps1` 可從官方原始碼重建 runtime，`mySQLPunk.csproj` 也會明確複製 `SQLite.Interop.dll` 的 x64/x86 runtime。
  - 完成內容：
    - 載入失敗訊息已改用語系化字串（`Connection.SpatiaLiteLoadFailed`），並同步更新狀態列，降級行為更清楚。
    - `其它 > 連線診斷` 會顯示 SpatiaLite runtime 目錄、`mod_spatialite.dll` 路徑、載入狀態與版本資訊。
  - 後續方向：若需要更完整的環境修復流程，可再加入一鍵重建 runtime 或下載指引。

### Table Designer 限制

- **自動補註解字典為遠端服務，可能受網路影響 ✅ 本機快取、匯入匯出、逐項差異預覽、命名字典版本比較與回復已補齊**
  - 現況：Table Designer 的「補註解」會載入遠端字典對照表；成功載入後會保存到本機快取。
  - 完成內容：若網路/站台/SSL 等因素導致遠端載入失敗，會在重試後改用上次成功的本機快取，避免補註解功能完全不可用；補註解進度視窗會標示目前使用的是遠端字典、本機快取、匯入字典或已命名字典；Table Designer 的補註解下拉選單可手動匯入 / 匯出 JSON 字典檔，方便離線環境或團隊共用欄位註解對照；匯出的字典會包含 `version`、`exportedAtUtc`、`source`、`entryCount` 與 `signatureSha256`，匯入仍相容舊版純 key/value JSON；匯入前會顯示新增、更新、移除與不變項目的摘要與逐項表格，也會顯示字典來源簽章是否有效，使用者可檢查每個欄位的目前註解與匯入註解，確認後才覆蓋本機字典；也可以將目前字典另存為命名字典，之後直接從下拉選單切換、重新命名或刪除；同名命名字典被覆蓋前會自動保留上一版，使用者可從下拉選單比較歷史版本差異，確認後回復指定版本。
  - 後續方向：若需要更完整的字典協作，可再加入審核流程或團隊共享來源。

- **既有資料表修改仍有不支援情境 ✅ provider ALTER 與進階索引 smoke test 已補齊**
  - 現況：部分 ALTER TABLE 操作會列入「目前不支援以下既有資料表變更」；PostgreSQL Table Designer 已支援 `schema.table` 形式的既有資料表 SQL 產生，不再固定套用 `public` schema；PostgreSQL / SQL Server / Oracle 的 FULLTEXT、SPATIAL 索引 SQL 產生已納入可重跑 smoke test。
  - 本輪補齊：PostgreSQL provider 會列出非 `public` schema 的 Table/View、Function 與 Trigger，並讓欄位、索引、資料瀏覽、列數、複製建表、View DDL 與批次寫入等主要操作依 `schema.table` 產生正確 SQL；QueryForm 資料表新增/更新/刪除與 Form1 共用物件 SQL（開啟查詢、Drop、Dump/DDL、資料產生、補註解）也會依 `schema.table` 寫入正確 schema；Table Designer 欄位修改、註解、Primary Key 變更與索引刪除的 SQL 預覽也會依目前資料表 schema 產生正確物件名稱；新增 View / Function 範本會沿用目前選取物件的 schema，避免在非預設 schema 工作時又產生 `public` / `dbo` 範本。
  - 本輪驗證：新增 MySQL / PostgreSQL / SQL Server / Oracle / SQLite 既有資料表 ALTER smoke test，覆蓋欄位改名、型別變更、NULL / DEFAULT、註解、新增欄位、MySQL 刪欄位，以及 SQLite 受限 ALTER 的重建表策略。
  - 後續方向：Primary Key 與 constraint 變更仍需依 provider 增加更多實機測試資料庫案例；進階索引已先以 SQL builder smoke test 固定語法輸出，後續仍可補實機建立/刪除案例。

- **FULLTEXT / SPATIAL 索引只支援部分 provider 與語法 ✅ 主要 provider 與 SQLite 專用精靈已補齊**
  - 現況：Table Designer 支援 MySQL FULLTEXT/SPATIAL、PostgreSQL FULLTEXT GIN 與 SPATIAL GiST、SQL Server Full-Text / Spatial、Oracle CTXSYS/MDSYS 索引 SQL 產生；SQLite FTS virtual table、RTree 與 SpatiaLite spatial index 不混入一般索引 UI，改由 database 右鍵選單的專用精靈產生 SQL。
  - 完成內容：新增與修改資料表流程都會依 provider 產生對應 FULLTEXT/SPATIAL 語法；MySQL 既有資料表 ALTER 已補上 `ADD SPATIAL INDEX`，索引註解也會套用 MySQL 字串 escape；SQLite 專用精靈可產生 FTS5 virtual table、RTree virtual table 與 `CreateSpatialIndex` SQL，並可直接執行。
  - 測試覆蓋：`tests/SmokeTests.cs` 已涵蓋 FTS5、RTree、SpatiaLite spatial index SQL 產生與 RTree 維度欄位驗證。

- **SQL Server DEFAULT constraint 變更仍有限制 ✅ 已補齊**
  - 現況：SQL Server 會把欄位 DEFAULT 存成 default constraint，修改時不能只用一般 `ALTER COLUMN` 覆蓋。
  - 完成內容：Table Designer 修改欄位 DEFAULT 時會先查 `sys.default_constraints` 找到實際 constraint name 後 drop，再以 `DF_<table>_<column>` 規則建立具名 DEFAULT constraint；SQL Server 預覽 SQL 分段執行時也會保留 `DECLARE` batch，避免變數 scope 被切壞。
  - Schema 支援：SQL Server provider 會列出所有 schema 的 Table/View；`dbo` 維持原表名顯示，非 `dbo` 會顯示為 `schema.table`。資料瀏覽、資料編輯、DDL/Dump、Table/View 複製、補註解與 Table Designer SQL 產生都會解析 schema，不再硬套 `dbo`。

- **Oracle Table Designer 對權限與物件狀態較敏感 ✅ 預覽提示已補齊**
  - 現況：已有多種診斷提示，例如權限不足、物件不存在、跨 schema 權限、語法不符。
  - 完成內容：Oracle SQL 預覽會在可執行 SQL 前加入註解提示，標示目標物件、直接授權需求（ALTER / CREATE INDEX / DROP / COMMENT 等）與分段執行方式；儲存判斷也改成檢查是否存在真正可執行 SQL，避免預覽註解誤擋操作。
  - 後續方向：若需要更完整的 Oracle 風險控管，可再加入實際權限偵測查詢與執行前逐步確認。

### 資料瀏覽與儲存限制

- **沒有 Primary Key 的資料表儲存風險較高 ✅ 唯讀選項與 optimistic WHERE 已補齊**
  - 現況：儲存更新/刪除時，若沒有 Primary Key，會先顯示風險警告；繼續儲存時會用可比對欄位的原始值建立 optimistic WHERE 條件，並避開 BLOB/geometry 這類大型二進位欄位；若沒有可安全比對欄位，會拒絕產生不安全 WHERE。各 provider 的 `ExecSQL` 會回傳影響列數，更新/刪除若影響 0 列會提示資料可能已被他人修改或刪除。
  - 完成內容：`選項 > 一般` 新增「沒有 Primary Key 的資料表以唯讀模式開啟」。啟用後，開啟無 Primary Key 的資料表會自動停用新增、刪除與儲存操作，並在狀態列提示原因。
  - 風險：資料列被其他人改過，或欄位包含浮點/大文字時，WHERE 仍可能因 provider 比對規則而不穩定。
  - 後續方向：若仍需要編輯無 Primary Key 資料表，可再補 provider 實機案例與更細緻的衝突差異提示。

- **BLOB/geometry 欄位操作 ✅ 分頁檢視與資料表模式串流匯出已補齊**
  - 現況：`byte[]` 欄位在結果表格中會先嘗試顯示為 `[Geometry] WKT`，無法解析時才顯示 `[BLOB n bytes] 0x...`；右鍵可檢視十六進位、複製 Hex、匯出檔案，在資料表資料模式可匯入檔案寫回目前 BLOB 欄位，也可針對 geometry 複製 WKT / WKT 轉 Geometry SQL。
  - 完成內容：BLOB 十六進位檢視器改為 4KB 分頁顯示，支援首頁、上一頁、下一頁、末頁與複製本頁 Hex，避免大型 BLOB 一次轉成完整文字造成 UI 卡頓。
  - 完成內容：資料表資料模式中，若目前資料列有 Primary Key 且尚未被本機修改，右鍵「匯出 BLOB 檔案」會以 provider-level `SequentialAccess` 重新查詢單一欄位並串流寫入檔案；MySQL、PostgreSQL、SQLite、SQL Server、Oracle provider 會共用同一個串流服務，匯出時狀態列會顯示已寫入大小。
  - 限制：任意 SQL 查詢結果仍會先載入 `DataTable`，因此超大型結果集的整批 streaming export 仍是後續方向；沒有 Primary Key 或資料列已有未儲存變更時，單一 BLOB 匯出會退回目前格子的記憶體值。

- **查詢結果匯出格式 ✅ 已補齊常用格式**
  - 現況：查詢結果匯出預設使用 CSV，並可在儲存對話框選擇 Excel `.xlsx`、TSV、JSON、XML、HTML 或 Markdown。
  - 完成內容：各格式會共用結果表格顯示值轉換；BLOB/geometry 會沿用結果表格的 `[Geometry] WKT` 或 `[BLOB n bytes]` 顯示，日期與空值也會一致處理。
  - 後續方向：若需要直接匯出大型查詢結果集，可再評估 provider-level streaming export，避免整份 DataTable 先載入記憶體；單一 BLOB/geometry 欄位的資料表模式匯出已先支援串流寫檔。

### Table/View 複製限制

- **View SQL 無法安全轉換時會改用 table snapshot ✅ 使用者選項與預覽已補齊**
  - 現況：跨 provider 複製 View 時，如果無法解析或轉換 SQL，會以查詢結果建立資料表快照。
  - 完成內容：複製前新增「跨 Provider 複製 View」對話框，讓使用者選擇：
    - **嘗試轉換 View SQL**（無法轉換時自動改為 table snapshot）
    - **直接建立 Table snapshot**（最穩定，不保留 View 語法）
    - 取消複製
    - 可在同一個對話框檢查來源 View SQL 與轉換後 SQL 預覽；若無法安全轉換，會顯示原因。
  - 方言轉換：已支援 SQL Server `TOP (n)` 轉 MySQL/PostgreSQL/SQLite `LIMIT n` 或 Oracle `FETCH FIRST`、MySQL/PostgreSQL/SQLite `LIMIT` 轉 SQL Server `TOP (n)` 或 Oracle `FETCH/OFFSET`，帶穩定 `ORDER BY` 的 MySQL/PostgreSQL/SQLite `LIMIT ... OFFSET` 可轉 SQL Server `OFFSET ... FETCH NEXT`，SQL Server / Oracle `OFFSET ... FETCH NEXT/FIRST` 可轉 MySQL/PostgreSQL/SQLite `LIMIT ... OFFSET`，簡單 Oracle `ROWNUM <= n` 轉目標 provider row limit，以及 `NVL` / `NVL2` / `IFNULL` / `ISNULL` / `GETDATE()` / `NOW()` / `SYSDATE` / `SYSTIMESTAMP` / `CURDATE()` / `CURRENT_DATE` / `DATE` / `TRUNC` / `DATEDIFF` / `DATEADD` / `DATE_ADD` / `DATE_SUB` / `YEAR` / `MONTH` / `DAY` / `HOUR` / `MINUTE` / `SECOND` / `DATEPART` / `EXTRACT` 的常見函式轉換；`DATEADD` / `DATE_ADD` / `DATE_SUB` 已涵蓋 year、month、day、hour、minute、second。
  - 進階轉換：已補上簡單 `DATE_FORMAT` / `FORMAT` / `TO_CHAR` / SQL Server `CONVERT(..., 23/120)` 日期格式函式、PostgreSQL `DATE_TRUNC` 與 Oracle `TRUNC(..., 'MM'/'YYYY'/'HH24')` 日期截斷、`IF` / `IIF` / `DECODE` 條件函式、`CEIL` / `CEILING` 數值進位函式、`MOD` 取餘數函式轉 SQL Server `%`、`POW` 次方函式轉 `POWER`、`GREATEST` / `LEAST` 雙參數比較函式轉 SQL Server `CASE WHEN`、`TRUE` / `FALSE` 布林常值轉 SQL Server / Oracle 數值常值、PostgreSQL `欄位::型別` cast operator 轉目標 provider `CAST(...)`、`ORDER BY ... NULLS FIRST/LAST` 轉 SQL Server / MySQL `CASE WHEN` 排序、`RAND` / `RANDOM` / `DBMS_RANDOM.VALUE` 隨機數函式、PostgreSQL `ILIKE` 大小寫不敏感比對、`REGEXP_LIKE` / PostgreSQL `~` 正規表示式比對、`GROUP_CONCAT` / `group_concat` / `STRING_AGG` / `LISTAGG`、`JSON_EXTRACT` / `JSON_VALUE` / `JSON_QUERY` 與 PostgreSQL `->` / `->>` / `#>` / `#>>` JSON operators 的跨 provider 轉換，並可在目標為 SQL Server / Oracle 時將 `WITH RECURSIVE` 轉成 `WITH`，讓常見日期格式、日期截斷、條件欄位、數值進位、最大/最小值比較、布林常值、型別轉換、空值排序、字串比對、正規表示式、字串聚合、JSON 純量讀取、JSON 片段讀取與遞迴 CTE 關鍵字差異可以保留 View SQL。
  - 已知情境：Oracle 階層查詢、MySQL 專用 View 語法、帶 OFFSET 且缺少穩定排序的 SQL Server 轉換、無法解析的 SELECT SQL 仍會改用 table snapshot。
  - 測試覆蓋：`tests/SmokeTests.cs` 已加入 TOP / LIMIT / LIMIT OFFSET / OFFSET FETCH / ROWNUM、日期格式（含 SQL Server `CONVERT` style 23/120）、目前日期/時間（含 `SYSDATE` / `SYSTIMESTAMP`）、`DATE_TRUNC` 與 Oracle `TRUNC(..., 'MM'/'YYYY'/'HH24')` 日期截斷、`NVL2` 空值條件、`DATE` / `TRUNC` 日期截斷、`DATEDIFF` 天數差、`DATEADD` / `DATE_ADD` / `DATE_SUB` 年/月/日/時加減與 `YEAR` / `MONTH` / `DAY` / `HOUR` / `MINUTE` / `SECOND` / `DATEPART` / `EXTRACT` 日期部分函式、`IF` / `IIF` / `DECODE` 條件函式、`CEIL` / `CEILING` 數值進位、`MOD` 取餘數、`POW` / `POWER` 次方、`GREATEST` / `LEAST` 最大/最小值比較、`TRUE` / `FALSE` 布林常值、PostgreSQL `欄位::型別` cast operator、`NULLS FIRST/LAST` 空值排序、`RAND` / `RANDOM` / `DBMS_RANDOM.VALUE` 隨機數、PostgreSQL `ILIKE`、`REGEXP_LIKE` / PostgreSQL `~` 正規表示式比對、字串聚合、`CONCAT` 字串串接、`LEN` / `LENGTH` / `CHAR_LENGTH` / `CHARACTER_LENGTH` 字串長度、`TRIM` / `LTRIM` / `RTRIM` 字串修剪、`SUBSTR` / `SUBSTRING` / `SUBSTRING ... FROM ... FOR` / `LEFT` / `RIGHT` 擷取字串、`LOCATE` / `CHARINDEX` / `INSTR` / `POSITION` 字串位置、JSON 純量讀取（含 `JSON_EXTRACT` / `JSON_VALUE` / PostgreSQL `->>` / `#>>` 轉 PostgreSQL / MySQL / Oracle）、JSON 片段讀取（含 `JSON_QUERY` / PostgreSQL `->` / `#>` 轉 MySQL / PostgreSQL / SQLite）、CTE/window 保留、`WITH RECURSIVE` 關鍵字轉換與不支援轉換原因的可重跑案例。
  - 後續方向：若需要更高相容性，可再逐 provider 擴充 JSON table 與更多 provider 專用內建函式等更複雜 SQL 方言轉換規則。

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
