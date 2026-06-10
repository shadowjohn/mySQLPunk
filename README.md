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

打包發布：

```powershell
.\scripts\package-release.ps1 -Version 1.0.0
```

此腳本會使用 Release 組態建置專案，將 `mySQLPunk/bin/Release` 整理成 `dist/mySQLPunk-<version>-win-x64-portable`，產生 portable zip 與 `release-manifest.json`，manifest 會包含檔名、大小與 SHA-256，方便上傳 GitHub Releases 後供程式內更新檢查與下載使用。打包時會一併放入 `THIRD_PARTY_NOTICES.md` 與可取得的 NuGet license/notice 檔，並排除不屬於程式必要 runtime 的 `sqlite3.exe`、`libreadline8.dll`、`libtermcap-0.dll`。

GitHub Actions 自動發版：

```powershell
# 1. 先確認 mySQLPunk/Properties/AssemblyInfo.cs 的 AssemblyVersion / AssemblyFileVersion
#    已更新成要發布的版本，例如 1.0.1.0。
git tag v1.0.1
git push origin v1.0.1
```

推送 `v*` tag 後，`.github/workflows/release.yml` 會在 GitHub 的 Windows runner 上還原 NuGet、用 MSBuild 編譯 Release、執行 `scripts/package-release.ps1`，並建立或更新 GitHub Release，上傳 portable zip 與 `release-manifest.json`。也可在 GitHub Actions 手動執行 `Release` workflow 並輸入版本號。Workflow 會檢查 tag / 手動輸入版本是否和 `AssemblyFileVersion` 一致，避免程式內更新檢查一直判定同一版本可更新。

備註：

- Repo 根目錄有提供 `NuGet.Config`，會強制將 NuGet 還原目錄固定在本專案的 `packages/`，避免受使用者全域 NuGet 設定影響導致 `..\packages\...` 找不到。

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
| 選項中心 | 部分可用 | 已補齊主要分類頁與 `application-options.json` 保存；查詢視窗已套用記錄限制、編輯器字型/換行/Tab 空格、自動完成開關、大型 SQL 停用編輯器輔助、資料表儲存自動交易、SQL 檔案位置、匯出位置、還原差異抽樣列數、結果網格字型與列高度、日期/時間與數字格式、工具提示顯示開關、診斷記錄、自動復原草稿、索引標籤開啟偏好、HTTP 代理與進階註冊設定。 |
| 應用程式更新 | 部分可用 | 已支援從 GitHub Releases 檢查最新版本，說明選單可手動檢查，並可依選項在啟動時背景檢查；若 release 附帶 installer asset，可從程式內下載並啟動安裝程式；若只有 portable zip，可從程式內下載、校驗並產生套用腳本，關閉目前程式後解壓覆蓋並重新啟動；若 release 附帶 `release-manifest.json` 且包含下載檔 SHA-256，開啟或套用前會先校驗雜湊；`scripts/package-release.ps1` 可產生 portable zip 與 manifest，正式 installer/updater 體驗仍可再強化。 |

## 未完成功能與已知限制

以下是從程式碼裡的「尚未支援」、「Unavailable」、「Unsupported」與實作 fallback 掃描出的清單。後續修改請優先參考這裡，把完成狀態同步更新。

### 優先待辦

- **選項中心分類功能 ✅ 查詢視窗、診斷記錄、自動復原草稿、索引標籤偏好、HTTP 代理、檢視顯示偏好與進階註冊已補齊**
  - 現況：選項視窗已具備一般、索引標籤、自動完成程式碼、編輯器、記錄、AI、自動復原、檔案位置、連線能力、環境與進階等分類。
  - 完成內容：`ApplicationOptionSettings` 會將通用選項保存到 `application-options.json`；查詢視窗已讀取並套用記錄限制、編輯器字型大小、換行、Tab 空格、自動完成啟用狀態、是否自動載入 metadata，且 SQL 文字超過「大型檔案停用門檻」時會停用語法上色與自動完成以降低卡頓；資料表資料模式儲存時可依「自動開始交易」把同批新增、修改、刪除包在 provider 對應的 BEGIN/COMMIT/ROLLBACK 流程中；匯出預設資料夾、SQL 開啟/儲存資料夾、結果網格字型大小、結果列高度、日期/時間格式、千分位與是否使用系統數字格式也已套用；檔案位置頁可設定還原差異內容指紋抽樣列數，用於控制還原前後大型資料表內容比對最多讀取幾列；一般頁的「顯示工具提示」會控制主要工具列、連線按鈕、收藏選單與 TreeView 節點提示是否顯示；一般頁的「啟動時自動檢查更新」會在啟動後背景查詢 GitHub Releases，說明選單也提供手動檢查更新；進階選項的「啟用診斷記錄」會把查詢歷程以 JSONL 寫入 `選項 > 檔案位置` 的記錄位置，內容保留 SQL 預覽與 SHA-256 指紋，不保存完整 SQL 原文；自動復原的查詢開關與間隔會啟動查詢草稿定時保存，草稿寫入查詢資料夾下的 `auto-recovery`；索引標籤設定可決定新查詢開在主視窗分頁、最後使用位置或新視窗，且「允許重複開啟相同的物件」關閉時會重用同名同型別分頁；檢視選單的導覽窗格（含僅顯示活躍物件）、資訊窗格、清單/詳細資料、欄位排序（含遞增/遞減）、欄位顯示、頂部即時篩選、「隱藏物件群組」與「顯示隱藏的項目」會保存並套用設定，其中僅顯示活躍物件會隱藏空的物件分類，隱藏物件群組會把 Tables / Views / Functions / Users / Events / Queries 的物件直接顯示在 database 節點下且保留右鍵與雙擊操作，隱藏項目會預設過濾 SQLite 系統表、SpatiaLite metadata、sidecar metadata 與常見 provider 系統物件；連線能力的 HTTP 代理設定會套用到 WebRequest/WebClient 路徑，例如自動補註解字典下載與共用 HTTP helper，且「測試連線能力」會以目前代理設定執行 HTTP 探測並依目前語系回報直接連線、代理模式、SOCKS5 限制與結果；進階註冊可在目前使用者層級註冊 SQL 檔案開啟方式與 `mysqlpunk://` URL 協定，關閉選項時只移除本程式建立的註冊項目。
  - 後續方向：部分選項仍需逐步接到實際行為，例如 SOCKS5/provider 原生資料庫連線代理。

- **應用程式打包與更新 ⚠️ 更新檢查、portable 打包與可攜版套用腳本已補齊**
  - 現況：`AppUpdateService` 可讀取 GitHub Releases latest API，解析 `tag_name`、版本、release notes、下載頁、installer asset 與 portable zip asset；主選單「說明 > 檢查更新...」可手動檢查，選項「啟動時自動檢查更新」會在啟動後背景檢查。
  - 完成內容：版本比較會支援 `v1.2.3` 這類 release tag，若發現新版且 release 內有 `.exe` / `.msi` / `.msix` / `.appinstaller` asset，會下載到暫存更新資料夾並啟動安裝程式；若沒有 installer 但有 `mySQLPunk` portable zip，會下載到暫存更新資料夾並詢問是否立即套用，選擇套用時會產生 PowerShell 腳本，等待目前程式結束後解壓 zip、覆蓋應用程式資料夾並重新啟動 `mySQLPunk.exe`，選擇不套用則維持開啟壓縮檔供手動更新；若 release 同時附帶 `release-manifest.json` 且 manifest 內有對應檔名的 SHA-256，會在開啟或套用前先比對下載檔雜湊，不符時停止並顯示錯誤；若 release 沒有可直接下載的更新檔，才會提示開啟 release 下載頁；尚未發布 release 或網路失敗時會在手動檢查顯示錯誤，背景檢查只更新狀態列避免干擾使用者；更新檢查與下載會套用選項中心的 HTTP/HTTPS 代理設定；`scripts/package-release.ps1` 可用 Release 組態建置並產生 portable zip 與 `release-manifest.json`，manifest 包含 SHA-256 與檔案大小，可作為 GitHub Releases 上傳素材。
  - 後續方向：正式打包仍可接 Velopack 或其他 installer 流程，讓安裝版更新支援更完整的差分、回復與版本控管。

- **連線群組與物件群組顯示 ✅ 已完成**
  - 觸發位置：左側樹狀清單空白處右鍵選單的「新增群組」，以及連線/群組節點右鍵選單的群組操作項目。
  - 完成內容：
    - 連線支援 `conn_group` 欄位，儲存與讀取已整合至 `setting.ini`。
    - 左側樹狀清單以群組節點分類顯示連線。
    - 右鍵選單支援「移至群組」、「移出群組」、「重新命名群組」、「刪除群組」。
    - 語系文字已補齊。
  - 備註：資料庫物件分類可由「檢視 > 顯示 > 隱藏物件群組」切換成扁平顯示；Tables / Views / Functions / Users / Events / Queries 的子物件會直接掛在 database 節點下，Backups / Models / BI / Other / Reports 這類操作入口仍保留群組，避免功能入口消失。

- **多連線設定檔 ✅ 已完成**
  - 觸發位置：連線根節點右鍵選單的「切換連線設定檔」。
  - 完成內容：
    - 保留既有 `setting.ini` 作為預設設定檔，不破壞舊版連線資料。
    - 新增的連線設定檔會儲存在 `connection_profiles/*.json`，目前作用中的設定檔記錄於 `connection-profile.txt`。
    - 右鍵選單可查看目前設定檔、切換既有設定檔，或新增空白設定檔並立即切換。
    - 支援複製目前設定檔；非預設設定檔可重新命名或刪除，刪除目前設定檔後會切回預設設定檔。
    - 切換設定檔前會先儲存目前設定並關閉已開啟的連線，避免跨 profile 共用舊連線狀態。

### Provider 與資料庫操作限制

- **Provider 功能支援表 ✅ 已語系化**
  - 現況：`其它 > 功能支援` 會依目前連線列出資料表、檢視、可編輯資料表資料、SQL 匯入/匯出、備份、Stored Functions 與 Triggers/Events 的支援狀態。
  - 完成內容：功能名稱、支援/不支援狀態、已載入數量、SQLite 備份方式、邏輯 SQL dump 與 SQLite 不儲存 database routines 等說明已改用繁中/英文語系字串。

- **維護檢查表 ✅ 已語系化**
  - 現況：`其它 > 維護檢查` 會列出連線狀態、資料表/檢視載入數、備份目標、開啟中的查詢分頁與最大資料表。
  - 完成內容：檢查項目、正常/警告/資訊狀態、資料表/檢視/分頁數量、最大資料表列數與無資料表 fallback 都會依繁中/英文語系切換。

- **連線診斷表 ✅ 已語系化**
  - 現況：`其它 > 連線診斷` 會列出 provider、連線狀態、資料庫、物件數量、備份來源與 SQLite/SpatiaLite runtime 狀態。
  - 完成內容：診斷項目、就緒/警告狀態與 SpatiaLite 載入狀態已改用語系字串，繁中與英文介面都會顯示一致文字。

- **模型總覽表 ✅ 已語系化**
  - 現況：`模型 > Schema Overview` 會列出資料表與檢視的欄位數、索引數、列數與狀態。
  - 完成內容：物件類型與就緒狀態已改用語系字串，繁中會顯示「資料表 / 檢視 / 就緒」，英文維持 `Table / View / Ready`。

- **模型欄位與索引目錄 ✅ 已語系化**
  - 現況：`模型 > Column Catalog` 會列出資料表/檢視欄位，`模型 > Index Catalog` 會列出索引或無明確索引狀態。
  - 完成內容：欄位目錄的物件類型與索引目錄的無索引 fallback 已改用語系字串，繁中與英文介面都會顯示一致文字。

- **報表與 BI 物件類型 ✅ 已語系化**
  - 現況：`查詢 > Object Inventory`、`Table Row Counts`、`BI > Object Distribution` 與 `BI > Row Count Ranking` 會顯示物件類型、分類與就緒狀態。
  - 完成內容：資料表/檢視類型、就緒狀態、物件分布分類與備份來源/目標類型已改用語系字串，繁中與英文介面都會顯示一致文字。

- **資料庫群組清單 ✅ 已修正並語系化**
  - 現況：左側資料庫底下的 Views、Backups、Models、BI、Other、Queries、Reports 等群組會在右側清單顯示對應項目。
  - 完成內容：群組清單會正確指定到右側資料表資料來源；檢視、模型、其它、查詢、報表、SQL 備份、缺少來源、空白與就緒狀態已改用語系字串。

- **資料庫搜尋結果 ✅ 已語系化**
  - 現況：資料庫搜尋會列出符合關鍵字的資料表、檢視與欄位結果。
  - 完成內容：搜尋結果的類型與位置已改用語系字串，繁中會顯示「資料表 / 檢視 / 欄位」，英文維持 `Table / View / Column`。

- **查詢歷程類型 ✅ 已語系化**
  - 現況：查詢歷程會列出執行時間、資料庫、類型、狀態、列數、耗時與 SQL 預覽。
  - 完成內容：類型欄的查詢與命令已改用語系字串，繁中會顯示「查詢 / 命令」，英文維持 `Query / Command`。

- **物件細節面板類型 ✅ 已語系化**
  - 現況：右側資訊面板會在檢視、模型、BI、其它工具與報表細節中顯示物件類型。
  - 完成內容：細節面板的 View、Model、BI、Other、Report 類型已改用語系字串，繁中與英文介面都會顯示一致文字。

- **物件細節面板錯誤訊息 ✅ 已語系化**
  - 現況：右側資訊面板載入資料表、檢視、事件、函式與使用者細節時，可能顯示載入失敗或找不到物件訊息。
  - 完成內容：載入失敗與事件 / 函式 / 使用者找不到訊息已改用語系字串，繁中與英文介面都會回饋一致文字。

- **我的最愛狀態列訊息 ✅ 已語系化**
  - 現況：加入、移除、開啟、找不到與清除我的最愛時會在主視窗狀態列顯示操作結果。
  - 完成內容：上述狀態列訊息已改用語系字串，繁中與英文介面都會顯示一致文字。

- **備份狀態列與未知錯誤 fallback ✅ 已語系化**
  - 現況：備份建立 / 失敗與 SQL 執行未知錯誤 fallback 會顯示在狀態列或例外訊息中。
  - 完成內容：備份建立含路徑、備份失敗與未知錯誤 fallback 已改用語系字串，繁中與英文介面都會顯示一致文字。

- **備份服務層錯誤訊息 ✅ 已語系化**
  - 現況：還原備份、遠端備份副本與刪除前備份封存服務會檢查備份檔案是否存在，以及 SQL / ZIP 備份是否可還原。
  - 完成內容：備份來源路徑缺失、SQLite 刪除前備份輸出路徑缺失、找不到備份檔案、空 SQL 備份與 ZIP 內沒有 SQL 項目的錯誤已改用語系字串，繁中與英文介面都會顯示一致文字。

- **連線匯入預覽缺檔錯誤 ✅ 已語系化**
  - 現況：匯入連線設定前會先建立差異預覽，來源檔不存在時會回報錯誤。
  - 完成內容：連線匯入預覽缺少來源檔的錯誤已改用語系字串，繁中與英文介面都會顯示一致文字。

- **匯出與診斷 service 參數錯誤 ✅ 已語系化**
  - 現況：資料庫備份匯出、查詢結果匯出、BLOB 串流匯出、SpatiaLite runtime 修復與 SQLite 欄位註解 XLSX 匯入會在缺少必要路徑或來源檔時回報錯誤。
  - 完成內容：輸出目標路徑必填、SQL 必填、SpatiaLite 修復腳本不存在與 SQLite 欄位註解 XLSX 檔案不存在等錯誤已改用語系字串，繁中與英文介面都會顯示一致文字。

- **更新、註冊與串流匯出錯誤 ✅ 已語系化**
  - 現況：應用程式更新檢查、進階註冊與串流匯出會在缺少 GitHub/release/下載路徑參數、應用程式路徑或遇到不支援格式/provider 時回報錯誤。
  - 完成內容：GitHub owner / repository、Release JSON、下載資料夾、檔案路徑、應用程式路徑、串流匯出格式與 provider 不支援等錯誤已改用語系字串，繁中與英文介面都會顯示一致文字。

- **連線設定檔管理錯誤 ✅ 已語系化**
  - 現況：複製、重新命名或刪除連線設定檔時，底層會阻擋覆蓋預設設定檔、重新命名預設設定檔與刪除預設設定檔。
  - 完成內容：預設設定檔已存在、不可重新命名、不可刪除與設定檔已存在等錯誤已改用語系字串，繁中與英文介面都會顯示一致文字。

- **連線開啟與物件複製錯誤 ✅ 已語系化**
  - 現況：連線開啟 service 會透過 provider factory 建立資料庫物件；Table/View 複製服務會檢查來源/目標連線、物件類型、來源欄位與 View DDL。
  - 完成內容：資料庫建立器未回傳連線物件、來源或目標資料庫未連線、只支援複製 Table/View、來源資料表沒有可複製欄位、View DDL 無法取得，以及複製流程進度與 fallback 訊息已改用語系字串，繁中與英文介面都會顯示一致文字。

- **Provider View DDL 解析錯誤 ✅ 已語系化**
  - 現況：MySQL、PostgreSQL、SQL Server、Oracle 與 SQLite provider 會在複製 View、重新命名 View 或跨 provider 建立 View 時解析來源 View DDL。
  - 完成內容：空 View DDL 與各 provider View DDL 解析失敗已改用語系字串，繁中與英文介面都會顯示一致文字，並已納入 smoke test 覆蓋壞 DDL 的實際例外路徑。

- **Metadata 載入與匯入審核紀錄錯誤 ✅ 已語系化**
  - 現況：資料庫展開時會分段載入 Tables、Views、Functions、Users 與 Events；連線匯入完成後會寫入本機 JSONL 審核紀錄。
  - 完成內容：各 metadata 分類載入失敗與匯入審核紀錄路徑缺失等錯誤已改用語系字串，繁中與英文介面都會顯示一致文字，並保留 provider 原始錯誤訊息方便排查。

- **Geometry / WKB 解析錯誤 ✅ 已語系化**
  - 現況：資料表結果中的 geometry 欄位可轉成 WKT，底層 WKB / SpatiaLite 解析器會檢查 byte order、geometry type、collection、polygon ring 與資料長度。
  - 完成內容：WKB byte order 無效、不支援 geometry 類型、SpatiaLite marker 無效、資料提前結束與集合/點/環數量過大等錯誤已改用語系字串，繁中與英文介面都會顯示一致文字。

- **SQLite 專用物件精靈驗證錯誤 ✅ 已語系化**
  - 現況：SQLite FTS / RTree / SpatiaLite 精靈會檢查 virtual table 名稱、欄位、RTree min/max 維度欄位與 spatial index 目標。
  - 完成內容：欄位必填、物件名稱必填與 RTree 維度欄位需成對等錯誤已改用語系字串，繁中與英文介面都會顯示一致文字。

- **備份排程與還原報表資料夾錯誤 ✅ 已語系化**
  - 現況：備份完整性排程會輸出驗證報表並可隔離異常備份，還原差異檢查會輸出內容掃描報表。
  - 完成內容：備份完整性報表資料夾、備份隔離資料夾與還原內容掃描報表資料夾缺失等錯誤已改用語系字串，繁中與英文介面都會顯示一致文字。

- **SQLite 欄位註解審核報表資料夾錯誤 ✅ 已語系化**
  - 現況：SQLite 欄位註解匯入前會建立新增、更新、移除與不變的審核報表。
  - 完成內容：審核報表輸出資料夾缺失錯誤已改用語系字串，繁中與英文介面都會顯示一致文字。

- **SQLite 欄位註解交換解析錯誤 ✅ 已語系化**
  - 現況：SQLite 欄位註解交換支援 JSON、XLSX、CSV、YAML 與 CLI 自動化匯入/匯出，會檢查必要參數、來源內容、欄位標題、工作表與 provider 類型。
  - 完成內容：CLI 參數缺值、必要參數缺失、SQLite 資料庫路徑缺失、空 JSON/CSV/YAML、無可用註解、CSV/XLSX 欄位或工作表缺失、非 SQLite 連線與輸入/輸出路徑缺失等錯誤已改用語系字串，繁中與英文介面都會顯示一致文字。

- **自動註解字典錯誤 ✅ 已語系化**
  - 現況：Table Designer 會下載遠端自動註解字典，也支援匯入、匯出、預覽與命名版本管理。
  - 完成內容：遠端字典空資料、字典格式錯誤、遠端載入失敗改用本機快取、未知錯誤、匯入/預覽來源路徑與匯出目標路徑缺失等錯誤已改用語系字串，繁中與英文介面都會顯示一致文字。

- **命令列介面依賴本機 CLI ✅ 偵測、匯入預覽與匯入後補密碼已補齊**
  - 現況：右鍵選單會依 provider 產生 `mysql`、`psql`、`sqlcmd`、`sqlite3` 或 `sqlplus` 指令並開啟命令提示字元。
  - 完成內容：
    - 已加入 CLI 可用性偵測（先透過 `where.exe`，再掃描 `PATH`）。找不到時會顯示安裝說明連結，不會直接開啟空白終端機。
    - `選項 > 環境` 可自訂 MySQL、PostgreSQL、SQL Server、Oracle、SQLite CLI 執行檔路徑；未設定時仍會使用 `PATH`。
    - 已補上密碼傳遞策略：MySQL、PostgreSQL、SQL Server 會透過 `MYSQL_PWD`、`PGPASSWORD`、`SQLCMDPASSWORD` 環境變數傳給 CLI，不把密碼寫進命令列參數；未儲存密碼時仍會保留 CLI 互動式密碼提示。Oracle `sqlplus` 目前仍採互動式密碼輸入，避免把密碼放進連線字串。
    - 連線密碼會儲存在 Windows Credential Manager；舊版 `setting.ini` / profile JSON 中的加密 `pwd` 會在讀取時自動遷移到 credential，並重寫設定檔清空 `pwd`。編輯連線時若清空密碼欄位，會同步清除對應 credential。
    - 未儲存密碼時，MySQL、PostgreSQL、SQL Server 會在開啟 CLI 前顯示一次性密碼輸入框；輸入的密碼只放入本次 process environment，不會回寫到連線設定或設定檔。
    - 匯入連線設定前會顯示差異預覽，依新增、更新、不變與只存在目前設定分類；使用者可選擇整包取代，也可只合併勾選的匯入連線，保留未勾選與本機既有連線；匯出的連線 JSON 會附帶來源 ID 與 SHA-256 來源簽章，匯入預覽會顯示簽章是否有效，並可將已確認的匯出來源加入本機信任來源白名單，方便辨識檔案是否被修改與是否來自已信任來源；預覽摘要也會附上團隊審核資訊，集中列出來源信任狀態、新增/更新筆數、本機只存在筆數、需補密碼筆數、匯入群組與變更目標，方便多位維護者在合併或取代前檢查；完成取代或合併後會把匯入動作、來源簽章狀態、選取項目、變更目標、匯入群組與需補密碼數寫入本機 JSONL 審核紀錄 `connection-import-review-log.jsonl`，方便事後追蹤。
    - 匯入連線設定後，若偵測到 MySQL、PostgreSQL、Oracle 或非 Windows 驗證的 SQL Server 連線缺少密碼，會詢問是否開啟批次補密碼視窗；使用者可一次補多筆密碼，密碼只會寫入 Windows Credential Manager，不會出現在匯入檔或設定檔明文中。
  - 後續方向：若要進一步強化跨機匯入，可再將本機 JSONL 審核紀錄同步到集中式儲存或簽核系統，讓多位維護者跨機追蹤誰已審核匯出來源。

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
    - 還原支援：資料庫與 Backups 節點右鍵可開啟「還原備份」，支援 `.sql` 與含 `.sql` 的 `.zip`；還原前會顯示 SQL 語句數、大小與目標資料庫，確認後會先建立還原前快照並壓縮保存到 `mySQLPunk/pre-restore-backups`，快照成功後才沿用 SQL 匯入執行器套用；還原完成後會比較還原前後的資料表、檢視、函式/程序與事件/Trigger 數量，並列出新增/移除的物件名稱；資料表列數與欄位 metadata 可用時，也會列出資料列數差異、欄位新增/移除與型別/NULL/DEFAULT/註解變更，讓使用者立即看到資料量、物件與 schema 摘要變化。
    - 遠端副本支援：`選項 > 檔案位置` 可設定遠端備份資料夾與保留份數；每次建立備份成功後會自動複製一份到該資料夾，適合 NAS、雲端同步資料夾或團隊共享磁碟；若同名檔案已存在，會自動加上流水號避免覆蓋，並只清理 mySQLPunk 產生樣式的舊備份檔，避免誤刪同資料夾其它檔案。
    - 完整性驗證：備份建立後會立即驗證本機檔案；SQL 備份需有可執行語句，ZIP 備份需包含可讀 SQL 或 SQLite 備份，SQLite 備份會執行 `PRAGMA integrity_check`；若有遠端副本，也會在複製後驗證副本，驗證失敗會回報錯誤。
    - 定期排程：`選項 > 檔案位置` 可開關「定期驗證備份完整性」並設定驗證間隔；主程式啟動後會在背景檢查本機刪除前備份、還原前快照與遠端副本資料夾，驗證結果只顯示在狀態列，不跳出干擾式通知；每次排程也會輸出 JSON 報表到文件目錄的 `mySQLPunk/backup-integrity-reports`，方便追查異常備份。
    - 異常隔離：`選項 > 檔案位置` 可選擇驗證失敗時自動隔離異常備份，並設定隔離區保留份數；開啟後排程會把失敗檔案移到文件目錄的 `mySQLPunk/backup-quarantine`，輸出隔離 manifest，並只清理 mySQLPunk 產生樣式的舊隔離檔，預設關閉以避免未經同意移動備份。
    - 隔離區還原入口：資料庫與 Backups 節點右鍵可開啟「還原隔離備份」，從 `backup-quarantine` 選擇被隔離的備份檔，若 manifest 保留原始路徑會預填還原位置；移回前會顯示隔離檔路徑、原始路徑、大小、二次完整性驗證結果與目標檔案差異預覽（原始路徑是否存在、大小變化與是否需覆蓋確認），還原動作只把備份檔移回指定位置，不會直接灌入資料庫，使用者可先檢查檔案後再走既有「還原備份」流程；也可使用「批次還原隔離備份」把有原始路徑的隔離檔移回原處，目標檔已存在時會略過不覆蓋；還原預覽、略過原因與不支援檔案類型等錯誤訊息會依目前語系顯示。
  - 完成內容：還原摘要已包含物件數、資料列數、欄位 schema 差異；資料表會在還原前後建立順序無關的 SHA-256 內容指紋，即使列數不變但資料值被改動，也會在「資料內容差異」中標示變更的資料表。內容指紋改用 provider 分頁讀取，每頁先計算 row hash 再組成摘要，避免一次把大型資料表整批載入記憶體；`選項 > 檔案位置` 可設定內容指紋最多抽樣列數，預設 10000 列，摘要會標示「比對」或「抽樣」覆蓋列數；還原完成後也會輸出 JSON 內容掃描報表到文件目錄的 `mySQLPunk/restore-content-scan-reports`，保留每張資料表的列數、抽樣列數、是否完整比對與前後 SHA-256 指紋，方便事後稽核。
  - 後續方向：若需要更完整的資料保護，可再加入指定主鍵範圍或背景排程完整掃描。

- **SQLite 欄位註解不支援 ✅ sidecar metadata 與交換格式已補齊**
  - 現況：SQLite 本身沒有欄位註解語法，因此 mySQLPunk 會使用 `__mysqlpunk_column_comments` sidecar metadata table 保存欄位註解。
  - 完成內容：SQLite provider 讀取欄位時會合併 sidecar 註解；Table Designer 新增/修改/重建資料表與資料庫/資料表補註解流程都會寫入 sidecar metadata。
  - 匯出支援：SQL Dump 的結構匯出會附帶 sidecar table 建立語句與目前資料表的欄位註解 `INSERT OR REPLACE`，跨環境還原後可保留 mySQLPunk 欄位註解；SQLite 資料庫、Tables 節點與單一資料表右鍵可匯出 / 匯入專用欄位註解 JSON、XLSX、CSV、YAML，匯入時會建立 sidecar table 並覆蓋匯入範圍內資料表的註解；XLSX 匯出模板會包含 `provider`、`database`、`table`、`column`、`type`、`not_null`、`default_value`、`comment` 欄位，方便人工審核或交給外部工具補註解；CLI 可用 `--sqlite-comments-export --database <sqlite> --output <json|xlsx|csv|yaml> [--table <name>]` 與 `--sqlite-comments-import --database <sqlite> --input <json|xlsx|csv|yaml> [--table <name>]` 進行自動化交換；匯入也支援第三方扁平陣列 JSON（`table` / `column` / `comment`）、table 內 columns 陣列格式、`table,column,comment` CSV、含額外輔助欄位的 XLSX 工作表與簡化 YAML comments 清單，並接受 `object_name` / `field_name` / `comment_text`、`entity` / `attribute` / `note` 等常見第三方模板別名，方便外部工具轉接；匯入前會比對目前 sidecar 註解與匯入內容，顯示新增、更新、移除與不變摘要，匯入成功後會輸出 JSON 審核報告到文件目錄的 `mySQLPunk/sqlite-column-comment-import-reviews`，方便追蹤實際覆蓋內容。
  - 後續方向：若需要和其它 SQLite 工具深度整合，可再把審核報告接到外部簽核或共同維護平台。

- **SpatiaLite extension 可能載入失敗 ✅ 診斷資訊與修復指引已補齊**
  - 現況：SQLite provider 會嘗試載入 SpatiaLite；環境缺少 extension 時會顯示載入錯誤。`tools/spatialite/Build-SpatiaLiteRuntime.ps1` 可從官方原始碼重建 runtime，`mySQLPunk.csproj` 也會明確複製 `SQLite.Interop.dll` 的 x64/x86 runtime。
  - 完成內容：
    - 載入失敗訊息已改用語系化字串（`Connection.SpatiaLiteLoadFailed`），並同步更新狀態列，降級行為更清楚；初始化 SpatiaLite metadata、未開啟 SQLite 連線、runtime 目錄缺失與 `mod_spatialite.dll` 缺失等診斷訊息也會依目前語系回饋。
    - `其它 > 連線診斷` 會顯示 SpatiaLite runtime 目錄、`mod_spatialite.dll` 路徑、載入狀態與版本資訊；若 extension 未載入，也會顯示 runtime manifest、manifest 來源摘要、manifest runtime 檔案 SHA-256/大小校驗結果、官方來源 zip 快取、離線來源包、建置腳本路徑、可直接執行的 PowerShell 修復命令、偏好快取/離線包的修復命令、修復 log 路徑、缺少 DLL 與目前載入錯誤，且 Ready / Warning / Info 狀態欄與來源快取、離線包、manifest 來源、檔案校驗摘要、修復指南等診斷細節會依目前語系顯示，方便依 `tools/spatialite/Build-SpatiaLiteRuntime.ps1` 從 Gaia-SINS 官方 `libspatialite-5.1.0.zip` 來源重建 runtime；建置腳本會把下載成功的來源 zip 快取到 `tools/spatialite/cache`，也可用 `-PreferCachedSource` 或 `-OfflinePackagePath` 在離線環境重建；在連線診斷表格雙擊 `SpatiaLite Repair Command` 會直接開啟 PowerShell 執行建置腳本，並用 `Tee-Object` 將進度輸出寫入文件目錄的 `mySQLPunk/spatialite-repair-logs`；修復腳本結束後會重置目前 SQLite provider 的 SpatiaLite 載入狀態、重新嘗試載入 extension，並刷新連線診斷表格與狀態列。
  - 後續方向：若需要更完整的部署體驗，可再加入預先打包的 runtime release 檔與自動校驗下載。

### Table Designer 限制

- **自動補註解字典為遠端服務，可能受網路影響 ✅ 本機快取、匯入匯出、逐項差異預覽、命名字典版本比較與回復已補齊**
  - 現況：Table Designer 的「補註解」會載入遠端字典對照表；成功載入後會保存到本機快取。
  - 完成內容：若網路/站台/SSL 等因素導致遠端載入失敗，會在重試後改用上次成功的本機快取，避免補註解功能完全不可用；補註解進度視窗會標示目前使用的是遠端字典、本機快取、匯入字典或已命名字典；Table Designer 的補註解下拉選單可手動匯入 / 匯出 JSON 字典檔，方便離線環境或團隊共用欄位註解對照；匯出的字典會包含 `version`、`exportedAtUtc`、`source`、`entryCount` 與 `signatureSha256`，匯入仍相容舊版純 key/value JSON；匯入前會顯示新增、更新、移除與不變項目的摘要與逐項表格，也會顯示字典來源簽章是否有效，使用者可檢查每個欄位的目前註解與匯入註解，確認後才覆蓋本機字典；也可以將目前字典另存為命名字典，之後直接從下拉選單切換、重新命名或刪除；同名命名字典被覆蓋前會自動保留上一版，使用者可從下拉選單比較歷史版本差異，確認後回復指定版本。
  - 後續方向：若需要更完整的字典協作，可再加入審核流程或團隊共享來源。

- **既有資料表修改仍有不支援情境 ✅ provider ALTER 與進階索引 smoke test 已補齊**
  - 現況：部分 ALTER TABLE 操作會列入「目前不支援以下既有資料表變更」；PostgreSQL Table Designer 已支援 `schema.table` 形式的既有資料表 SQL 產生，不再固定套用 `public` schema；PostgreSQL / SQL Server / Oracle 的 FULLTEXT、SPATIAL 索引 SQL 產生已納入可重跑 smoke test。
  - 本輪補齊：Table Designer 載入既有資料表索引 metadata 失敗時不再沉默吞錯，會顯示可理解的警告訊息，索引頁改以空白狀態開啟，欄位設計仍可繼續使用。
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

- **Oracle Table Designer 對權限與物件狀態較敏感 ✅ 預覽提示、權限診斷 SQL、逐步確認清單與高風險二次確認已補齊**
  - 現況：已有多種診斷提示，例如權限不足、物件不存在、跨 schema 權限、語法不符。
  - 完成內容：Oracle SQL 預覽會在可執行 SQL 前加入註解提示，標示目標物件、直接授權需求（ALTER / CREATE INDEX / DROP / COMMENT 等）與分段執行方式；預覽註解也會依 SQL 變更類型產生逐步確認清單，提醒欄位改名、型別/NULL/DEFAULT 變更、欄位註解、索引與 constraint / Primary Key 等執行前檢查；同時附上 `all_tab_privs` 與 `session_privs` 權限診斷 SQL，方便使用者在套用 DDL 前先確認目前帳號對目標物件的直接授權與系統權限；若儲存 SQL 偵測到 Oracle `DROP COLUMN`、`DROP INDEX` 或 `DROP CONSTRAINT`，會在一般 SQL 確認後再顯示一次高風險 DDL 確認；儲存判斷也改成檢查是否存在真正可執行 SQL，避免預覽註解誤擋操作；Oracle DDL 執行失敗時會嘗試查詢並解析 `all_tab_privs` / `session_privs` 結果，把目前直接授權、Session 系統權限與可能缺少的權限摘要加入錯誤訊息；若偵測到權限不足、物件不可見或 tablespace quota 問題，錯誤訊息會附上可操作修復建議，包含確認 `SESSION_USER` / `CURRENT_SCHEMA`、檢查 `session_roles`、直接授權範例 SQL、DBA 最小授權/配額範本，以及跨 schema DDL policy 提醒；常見 Oracle 實機錯誤碼也已補上提示，包含 `ORA-00904`、`ORA-01439`、`ORA-01408`、`ORA-02264`、`ORA-02261`、`ORA-01950` 與既有權限/物件/constraint 類錯誤。
  - 後續方向：若需要更完整的 Oracle 風險控管，可再補更多實機 DDL 案例與團隊核准流程整合。

### 資料瀏覽與儲存限制

- **沒有 Primary Key 的資料表儲存風險較高 ✅ 唯讀選項、optimistic WHERE 與衝突值提示已補齊**
  - 現況：儲存更新/刪除時，若沒有 Primary Key，會先顯示風險警告；繼續儲存時會用可比對欄位的原始值建立 optimistic WHERE 條件，並避開 BLOB/geometry 這類大型二進位欄位；若沒有可安全比對欄位，會拒絕產生不安全 WHERE。各 provider 的 `ExecSQL` 會回傳影響列數，更新/刪除若影響 0 列會提示資料可能已被他人修改或刪除，並附上操作類型與 WHERE 比對條件摘要，方便判斷是哪個 optimistic 條件未命中。
  - 完成內容：`選項 > 一般` 新增「沒有 Primary Key 的資料表以唯讀模式開啟」。啟用後，開啟無 Primary Key 的資料表會自動停用新增、刪除與儲存操作，並在狀態列提示原因；若使用者選擇繼續編輯，衝突訊息會補充 UPDATE/DELETE、比對條件摘要與參數值（包含原始比對值與本次異動值），避免只看到泛用錯誤。若 UPDATE/DELETE 影響 0 列，QueryForm 會用未被本機修改且可安全比對的原始欄位重查候選資料列；剛好找到一列時會顯示原始值與目前資料庫值的欄位差異，找到 0 列或多列時會明確提示無法安全判斷。
  - 風險：資料列被其他人改過，或欄位包含浮點/大文字時，WHERE 仍可能因 provider 比對規則而不穩定。
  - 後續方向：若仍需要編輯無 Primary Key 資料表，可再補更多 provider 實機案例與衝突情境測試。

- **BLOB/geometry 欄位操作 ✅ 分頁檢視、資料表模式串流匯出與查詢結果底層串流匯出已補齊**
  - 現況：`byte[]` 欄位在結果表格中會先嘗試顯示為 `[Geometry] WKT`，無法解析時才顯示 `[BLOB n bytes] 0x...`；右鍵可檢視十六進位、複製 Hex、匯出檔案，在資料表資料模式可匯入檔案寫回目前 BLOB 欄位，也可針對 geometry 複製 WKT / WKT 轉 Geometry SQL。
  - 完成內容：BLOB 十六進位檢視器改為 4KB 分頁顯示，支援首頁、上一頁、下一頁、末頁與複製本頁 Hex，避免大型 BLOB 一次轉成完整文字造成 UI 卡頓。
  - 完成內容：資料表資料模式中，若目前資料列有 Primary Key 且尚未被本機修改，右鍵「匯出 BLOB 檔案」會以 provider-level `SequentialAccess` 重新查詢單一欄位並串流寫入檔案；MySQL、PostgreSQL、SQLite、SQL Server、Oracle provider 會共用同一個串流服務，匯出時狀態列會顯示已寫入大小。
  - 完成內容：`QueryResultExportService` 新增 provider-level streaming export 底層能力，可直接用 `DbDataReader` 將任意查詢結果輸出為 CSV / TSV / JSON / XML / HTML / Markdown，不必先載入整份 `DataTable`，並沿用既有 BLOB/geometry 預覽格式、空值處理與列數進度回報；查詢視窗匯出上述文字格式時，若上一個成功結果是一般查詢且可安全重跑，會直接走串流匯出，資料表編輯模式與 XLSX 則保留既有 `DataTable` 路徑。
  - 限制：XLSX 仍需整份結果資料才能產生活頁簿輸出。沒有 Primary Key 或資料列已有未儲存變更時，單一 BLOB 匯出會退回目前格子的記憶體值。

- **查詢結果匯出格式 ✅ 已補齊常用格式**
  - 現況：查詢結果匯出預設使用 CSV，並可在儲存對話框選擇 Excel `.xlsx`、TSV、JSON、XML、HTML、Markdown 或 SQL INSERT `.sql`。
  - 完成內容：各格式會共用結果表格顯示值轉換；BLOB/geometry 會沿用結果表格的 `[Geometry] WKT` 或 `[BLOB n bytes]` 顯示，日期與空值也會一致處理；XLSX 匯出會凍結標題列、套用自動篩選、穩定欄寬與表頭樣式，讓大型 workbook 開啟後可直接篩選與辨識欄位；SQL INSERT 匯出會產生 `query_result` 目標表的 `INSERT INTO` 腳本，字串、NULL、數字與 BLOB 十六進位 literal 會正確輸出，且可走串流匯出大型結果；匯出完成後會顯示摘要視窗，列出格式、筆數、檔案大小與路徑，並可直接開啟檔案或所在資料夾；成功匯出後會記住這次選擇的資料夾，下次匯出會直接從同一個位置開始。
  - 後續方向：若需要更完整的大型結果集匯出，可再評估 XLSX 分段寫入；單一 BLOB/geometry 欄位的資料表模式匯出已先支援串流寫檔。

### Table/View 複製限制

- **View SQL 無法安全轉換時會改用 table snapshot ✅ 使用者選項與預覽已補齊**
  - 現況：跨 provider 複製 View 時，如果無法解析或轉換 SQL，會以查詢結果建立資料表快照。
  - 完成內容：複製前新增「跨 Provider 複製 View」對話框，讓使用者選擇：
    - **嘗試轉換 View SQL**（無法轉換時自動改為 table snapshot）
    - **直接建立 Table snapshot**（最穩定，不保留 View 語法）
    - 取消複製
    - 可在同一個對話框檢查來源 View SQL 與轉換後 SQL 預覽；若無法安全轉換，會顯示原因。
  - 方言轉換：已支援 SQL Server `TOP (n)` 轉 MySQL/PostgreSQL/SQLite `LIMIT n` 或 Oracle `FETCH FIRST`、MySQL/PostgreSQL/SQLite `LIMIT` 轉 SQL Server `TOP (n)` 或 Oracle `FETCH/OFFSET`，帶穩定 `ORDER BY` 的 MySQL/PostgreSQL/SQLite `LIMIT ... OFFSET` 可轉 SQL Server `OFFSET ... FETCH NEXT`，SQL Server / Oracle `OFFSET ... FETCH NEXT/FIRST` 可轉 MySQL/PostgreSQL/SQLite `LIMIT ... OFFSET`，標準 / Oracle / PostgreSQL `FETCH FIRST n ROWS ONLY` 可轉 SQL Server `TOP (n)` 或 MySQL/PostgreSQL/SQLite `LIMIT n`，簡單 Oracle `ROWNUM <= n` 轉目標 provider row limit，以及 `NVL` / `NVL2` / `IFNULL` / `ISNULL` / `GETDATE()` / `GETUTCDATE()` / `SYSDATETIME()` / `SYSUTCDATETIME()` / `NOW()` / `CURRENT_TIMESTAMP` / `CURRENT_TIMESTAMP()` / `UTC_TIMESTAMP()` / `SYSDATE` / `SYSTIMESTAMP` / `CURDATE()` / `CURRENT_DATE` / `CURTIME()` / `CURRENT_TIME` / `LOCALTIME` / `LOCALTIMESTAMP` / `CURRENT_USER` / `SESSION_USER` / `SYSTEM_USER` / `USER()` / `DATABASE()` / `CURRENT_DATABASE()` / `DB_NAME()` / `SCHEMA()` / `CURRENT_SCHEMA` / `SCHEMA_NAME()` / `DATE` / `TRUNC` / `DATEDIFF` / `TIMESTAMPDIFF` / `MONTHS_BETWEEN` / `DATEADD` / `DATE_ADD` / `DATE_SUB` / `ADD_MONTHS` / `EOMONTH` / `LAST_DAY` / `DATEFROMPARTS` / `YEAR` / `MONTH` / `DAY` / `HOUR` / `MINUTE` / `SECOND` / `DATEPART` / `DATE_PART` / `EXTRACT` 的常見函式轉換；`DATEDIFF` / `TIMESTAMPDIFF` / `MONTHS_BETWEEN` 已涵蓋 year、quarter、month、week、day、hour、minute、second 或月份差距，`DATEADD` / `DATE_ADD` / `DATE_SUB` / `ADD_MONTHS` 已涵蓋 year、quarter、month、week、day、hour、minute、second 或月份加減。
  - 進階轉換：已補上簡單 `DATE_FORMAT` / `FORMAT` / `TO_CHAR` / `TO_DATE` / `TO_TIMESTAMP` / `STR_TO_DATE` / SQL Server `CONVERT(..., 23/120)` 日期格式與解析函式、PostgreSQL `DATE_TRUNC` 與 Oracle `TRUNC(..., 'MM'/'YYYY'/'HH24'/'MI')` 日期截斷，且 `DATE_TRUNC` 已涵蓋 year、month、day、hour、minute、second；`IF` / `IIF` / `DECODE` 條件函式、`CEIL` / `CEILING` 數值進位函式、`TRUNCATE(number, decimals)` 與 SQL Server `ROUND(number, decimals, 1)` 數值截斷函式、`MOD` 取餘數函式轉 SQL Server `%`、`POW` 次方函式轉 `POWER`、`GREATEST` / `LEAST` 雙參數比較函式轉 SQL Server `CASE WHEN`、`TRUE` / `FALSE` 布林常值轉 SQL Server / Oracle 數值常值、PostgreSQL `欄位::型別` cast operator 與 SQL Server `TRY_CAST` / `TRY_CONVERT` 轉目標 provider `CAST(...)` 或日期解析、`ORDER BY ... NULLS FIRST/LAST` 轉 SQL Server / MySQL `CASE WHEN` 排序、`RAND` / `RANDOM` / `DBMS_RANDOM.VALUE` 隨機數函式、PostgreSQL `ILIKE` 大小寫不敏感比對、`REGEXP_LIKE` / PostgreSQL `~` 正規表示式比對、`GROUP_CONCAT` / `group_concat` / `STRING_AGG` / `LISTAGG`、`CONCAT` 與 PostgreSQL / Oracle / SQLite `||` 字串串接、`LPAD` / `RPAD` 字串補齊轉 SQL Server / SQLite 等價語法、`JSON_EXTRACT` / `JSON_UNQUOTE(JSON_EXTRACT(...))` / `JSON_VALUE` / `JSON_QUERY` / `JSON_EXISTS` / `JSON_CONTAINS_PATH` / `JSON_LENGTH` / `JSON_ARRAY_LENGTH`、`JSON_OBJECT` / `JSON_ARRAY` 與 PostgreSQL `jsonb_build_object` / `jsonb_build_array`、SQLite `json_object` / `json_array`、Oracle `JSON_OBJECT KEY VALUE` 建構式，以及 MySQL / PostgreSQL `->` / `->>`、PostgreSQL `#>` / `#>>` JSON operators 的跨 provider 轉換；簡單 `JSON_TABLE(... COLUMNS (欄位 型別 PATH '$.x'))` 也可轉 PostgreSQL `LATERAL jsonb_array_elements` 逐欄位套用 PATH、SQL Server `OPENJSON ... WITH`，或 SQLite `json_each` 搭配欄位參照改寫；MySQL/Oracle `FOR ORDINALITY` 欄位已可轉 PostgreSQL `WITH ORDINALITY` 與 SQLite `json_each.key + 1`，SQL Server 會在尚無安全等價展開時保留明確不支援原因；並可在目標為 SQL Server / Oracle 時將 `WITH RECURSIVE` 轉成 `WITH`，讓常見日期格式、日期解析、日期截斷、條件欄位、數值進位、數值截斷、JSON_TABLE 序號欄位、最大/最小值比較、布林常值、型別轉換、空值排序、字串比對、正規表示式、字串聚合、字串補齊、JSON 純量讀取、JSON 片段讀取、JSON path 存在判斷、JSON 陣列長度、JSON 物件/陣列建構、JSON 陣列展開與遞迴 CTE 關鍵字差異可以保留 View SQL。
  - 本輪補齊：SQL Server `CONVERT(type, expr)` 一般型別轉換會在目標為 MySQL、PostgreSQL、Oracle 或 SQLite 時轉為對應 `CAST(expr AS type)`，補齊不帶日期 style 的跨 provider 轉型。
  - 本輪補齊：SQL Server `CONVERT(type, expr)` 一般型別轉換改用函式呼叫掃描器，可處理 `CONVERT(int, REPLACE(col, '-', ''))` 這類巢狀參數，並保留字串常值中的函式範例文字。
  - 本輪補齊：SQL Server `TRY_CAST(expr AS type)` 改用函式呼叫掃描器與 top-level `AS` 解析，可處理 `TRY_CAST(REPLACE(col, '-', '') AS int)` 這類巢狀參數，並保留字串常值中的函式範例文字。
  - 本輪補齊：SQL Server `TRY_CONVERT(type, expr[, style])` 改用函式呼叫掃描器，可處理 `TRY_CONVERT(int, REPLACE(col, '-', ''))` 這類巢狀參數，並保留字串常值中的函式範例文字；日期 style 23/120 仍會轉為目標 provider 日期解析函式。
  - 本輪補齊：`IF(...)` / `IIF(...)` / `DECODE(...)` / `CHOOSE(...)` 條件函式改用函式呼叫掃描器，可處理 `IIF(ABS(score) >= 60, ...)` 這類巢狀條件，並保留字串常值中的函式範例文字。
  - 本輪補齊：`GREATEST(left, right)` / `LEAST(left, right)` 複製到 SQLite 時會轉為通用 `CASE WHEN ... THEN ... ELSE ... END`，不再只處理 SQL Server 目標。
  - 本輪補齊：`DATEPART` / `DATE_PART` / `EXTRACT` / MySQL 日期部分函式已擴充 `quarter`、`week`、`weekday` 與 `dayofyear`，SQLite 會用 `strftime('%m')` 計算季度、`strftime('%W')` 計算週序、`strftime('%w')` 計算星期序、`strftime('%j')` 計算年內日序；Oracle 目標會改用 `TO_NUMBER(TO_CHAR(...))` 保留這些日期部分。
  - 本輪補齊：`DATEADD(...)`、`DATE_ADD(... INTERVAL ... ...)` 與 `DATE_SUB(... INTERVAL ... ...)` 的 interval 數量可使用欄位或簡單運算式，不再只支援純數字；SQLite 目標會產生動態 date/datetime modifier。
  - 本輪補齊：`DATEPART(...)` / `DATE_PART(...)` 改用函式呼叫掃描器，可處理 `DATEPART(day, DATEADD(day, 1, created_at))` 這類巢狀日期運算；`DATEADD` / `DATE_ADD` / `DATE_SUB` / `ADD_MONTHS` 轉換也會避開字串常值，保留報表範例文字。
  - 本輪補齊：SQL Server `DATEFROMPARTS(year, month, day)` 改用函式呼叫掃描器，可處理 `DATEFROMPARTS(ABS(year_col), month_col, day_col)` 這類巢狀參數，並保留字串常值中的函式範例文字。
  - 本輪補齊：`EOMONTH(...)` / `LAST_DAY(...)` 月末日期函式改用函式呼叫掃描器，可處理含 offset 或巢狀日期加減的月末轉換，並保留字串常值中的函式範例文字。
  - 本輪補齊：MySQL `CURTIME()` 與標準 `CURRENT_TIME` 會依目標 provider 轉為對應的目前時間表達式，SQL Server 使用 `CAST(GETDATE() AS time)`、SQLite 使用 `time('now')`、MySQL 使用 `CURTIME()`。
  - 本輪補齊：標準 `LOCALTIME` / `LOCALTIMESTAMP` 會依目標 provider 轉為目前時間或目前 timestamp 表達式，並先處理 `LOCALTIMESTAMP` 以避免被 `LOCALTIME` 前綴誤轉。
  - 本輪補齊：SQL Server `SYSDATETIME()` / `SYSUTCDATETIME()` 會依目標 provider 轉為目前 timestamp 或 UTC timestamp 表達式，與既有 `GETDATE()` / `GETUTCDATE()` 共用同一組轉換語意。
  - 本輪補齊：標準 `CURRENT_TIMESTAMP` / `CURRENT_TIMESTAMP()` 會依目標 provider 正規化為對應目前 timestamp 表達式，MySQL 目標統一輸出 `NOW()`。
  - 本輪補齊：`CURRENT_USER` / `SESSION_USER` / `SYSTEM_USER` / `USER()` 會依目標 provider 轉為對應目前使用者或登入者表達式，Oracle 目標的 session/system user 使用 `SYS_CONTEXT('USERENV','SESSION_USER')`。
  - 本輪補齊：`DATABASE()` / `CURRENT_DATABASE()` / `DB_NAME()` 會依目標 provider 轉為目前資料庫名稱表達式，Oracle 目標使用 `SYS_CONTEXT('USERENV','DB_NAME')`，SQLite 目標以 `NULL` 保持可執行。
  - 本輪補齊：`SCHEMA()` / `CURRENT_SCHEMA` / `SCHEMA_NAME()` 會依目標 provider 轉為目前 schema 表達式，SQL Server 目標使用 `SCHEMA_NAME()`，Oracle 目標使用 `SYS_CONTEXT('USERENV','CURRENT_SCHEMA')`，SQLite 目標以 `NULL` 保持可執行。
  - 本輪補齊：Oracle `TO_NUMBER(...)` / `TO_NUMBER(..., 'format')` 會在目標為 SQL Server、MySQL、PostgreSQL 或 SQLite 時轉為對應的 `CAST(...)` 數值型別，避免 Oracle View 複製到其它 provider 後留下不可執行的數值轉型函式。
  - 本輪補齊：`TO_NUMBER(...)` 改用函式呼叫掃描器，可處理 `TO_NUMBER(REPLACE(col, ',', ''), 'format')` 這類巢狀參數，並保留字串常值中的函式範例文字。
  - 本輪補齊：MySQL `TRUNCATE(number, decimals)` 複製到 SQL Server 會轉為 `ROUND(number, decimals, 1)`，複製到 PostgreSQL / Oracle 會轉為 `TRUNC(number, decimals)`，SQLite 會用整數截斷與 `pow(10, decimals)` 模擬，避免報表 View 留下 MySQL 專用數值函式。
  - 本輪補齊：SQL Server `ROUND(number, decimals, 1)` 的截斷語意會依目標 provider 轉為 MySQL `TRUNCATE(number, decimals)`、PostgreSQL / Oracle `TRUNC(number, decimals)`，SQLite 仍以整數截斷與 `pow(10, decimals)` 模擬；一般 `ROUND(number, decimals)` 不會被誤改。
  - 本輪補齊：`TRUNCATE(...)` 與三參數 `ROUND(..., ..., 1)` 改用函式呼叫掃描器，可處理 `TRUNCATE(ABS(col), 2)` 這類巢狀參數，並保留字串常值中的函式範例文字。
  - 本輪補齊：`CEIL(...)` / `CEILING(...)` 名稱轉換會避開字串常值，`MOD(...)` 轉 SQL Server `%` 時也改用函式呼叫掃描器，可處理 `MOD(ABS(col), n)` 這類巢狀參數並保留 `'CEIL(...)'` / `'MOD(...)'` 文字。
  - 本輪補齊：`POW(...)` 轉 `POWER(...)` 時只會改寫 SQL 片段，會保留 `'POW(...)'` 這類字串常值，避免報表標籤或樣板文字被誤改。
  - 本輪補齊：`RAND()` / `RANDOM()` / `DBMS_RANDOM.VALUE` 隨機數函式轉換會避開字串常值，避免將 `'RAND()'` 這類報表文字誤改成目標 provider 函式名稱。
  - 本輪補齊：`NVL(...)` / `IFNULL(...)` 轉換為 `COALESCE(...)` 時只會改寫 SQL 片段，不會再誤改 `'NVL(...)'` 或 `'IFNULL(...)'` 這類字串常值，避免備註、狀態文字或樣板字串被污染。
  - 本輪補齊：`NVL2(...)` / `ISNULL(...)` 轉換會用函式呼叫掃描器避開字串常值，並可處理參數字串內含括號或巢狀函式的情境，避免漏轉真正函式或污染 `'NVL2(...)'` / `'ISNULL(...)'` 文字。
  - 本輪補齊：`LOCATE` / `CHARINDEX` / `INSTR` 的起始位置參數會轉為目標 provider 等價語法；SQLite / PostgreSQL 會用 `SUBSTR` / `SUBSTRING` 搭配 `CASE` 保留找不到時回傳 0 的行為。
  - 本輪補齊：MySQL `UCASE(...)` / `LCASE(...)` 會在複製到 SQL Server、PostgreSQL、SQLite 或 Oracle 時轉為通用的 `UPPER(...)` / `LOWER(...)`，並改用函式呼叫掃描器處理巢狀參數與保留字串常值，避免目標資料庫留下不可執行的 MySQL alias 或污染報表文字。
  - 本輪補齊：`TRIM(...)` / `LTRIM(RTRIM(...))` / `RTRIM(LTRIM(...))` 字串修剪轉換改用函式呼叫掃描器，可處理 `REPLACE(...)` 這類含逗號的巢狀參數，並保留字串常值中的函式範例文字。
  - 本輪補齊：SQL Server `DATALENGTH(...)`、PostgreSQL/MySQL `OCTET_LENGTH(...)` 與 Oracle `LENGTHB(...)` 會依目標 provider 轉為對應的 byte-length 函式，SQLite 目標會用 `length(CAST(expr AS BLOB))` 保留位元組長度語意。
  - 本輪補齊：MySQL/PostgreSQL `BIT_LENGTH(...)` 會依目標 provider 保留位元長度語意，SQL Server / Oracle / SQLite 目標會用對應 byte-length 函式乘以 8。
  - 本輪補齊：SQL Server `NEWID()`、MySQL `UUID()` 與 Oracle `SYS_GUID()` 會依目標 provider 轉為對應的 UUID/Guid 產生函式，SQLite 目標會用 `randomblob()` 組出 v4-like UUID 字串。
  - 本輪補齊：MySQL/PostgreSQL `REPEAT(...)` 與 SQL Server `REPLICATE(...)` 會依目標 provider 轉為 `REPEAT`、`REPLICATE`、SQLite `ZEROBLOB` 模擬或 Oracle `RPAD` 模擬，讓常見補零、遮罩與固定字元重複 View 可跨資料庫複製。
  - 本輪補齊：MySQL `LPAD(...)` / `RPAD(...)` 複製到 SQL Server 或 SQLite 時改用函式呼叫掃描器，可支援巢狀參數並保留字串常值中的函式範例文字。
  - 本輪補齊：SQL Server/MySQL `SPACE(n)` 會在目標為 PostgreSQL、SQLite 或 Oracle 時轉為對應的字串重複表達式，讓縮排、補空白與固定格式 View 可跨資料庫複製。
  - 本輪補齊：Oracle/PostgreSQL `CHR(n)` 與 SQL Server/MySQL/SQLite `CHAR(n)` 會依目標 provider 轉為對應的字元碼函式，且會避開 `CAST(... AS CHAR(n))` 這類型別宣告，避免誤改欄位型別。
  - 本輪補齊：SQL Server `NCHAR(n)` 會依目標 provider 轉為對應的 Unicode 字元碼函式，且一般 `CAST(... AS NCHAR/VARCHAR/INT/DATE...)` 也會依目標 provider 轉為相容型別，不再只處理 `CONVERT(...)` / `TRY_CAST(...)` / `TRY_CONVERT(...)`。
  - 本輪補齊：MySQL/PostgreSQL/Oracle `ASCII(...)`、SQL Server `UNICODE(...)` 與 SQLite `unicode(...)` 會依目標 provider 轉為對應的字元碼讀取函式，讓字元碼分析 View 可跨資料庫複製。
  - 本輪補齊：MySQL 一參數 `ISNULL(expr)` 會依目標 provider 轉為 `expr IS NULL` 或 `CASE WHEN expr IS NULL THEN 1 ELSE 0 END`，SQL Server 二參數 `ISNULL(expr, fallback)` 仍會轉為 `COALESCE(expr, fallback)`；避免跨 provider 複製時把 NULL 判斷誤轉成空值替代。
  - 本輪補齊：日期格式化與解析函式轉換改用函式呼叫掃描器，`CONVERT(..., style 23/120)`、`DATE_FORMAT`、`FORMAT`、`TO_CHAR`、`TO_DATE`、`TO_TIMESTAMP` 與 `STR_TO_DATE` 會保留字串常值中的範例文字，只改寫真正的 SQL 函式呼叫。
  - 本輪補齊：MySQL `FIELD(expr, value1, value2, ...)` 會在目標非 MySQL 時轉為通用 `CASE expr WHEN value1 THEN 1 ... ELSE 0 END`，讓自訂狀態排序與優先序 View 可跨資料庫複製。
  - 本輪補齊：MySQL `ELT(index, value1, value2, ...)` 會在目標非 MySQL 時轉為通用 `CASE index WHEN 1 THEN value1 ... ELSE NULL END`，讓代碼轉標籤與優先序文字 View 可跨資料庫複製。
  - 本輪補齊：MySQL `FIND_IN_SET(expr, 'value1,value2,...')` 在第二參數為靜態清單時會轉為通用 `CASE expr WHEN value1 THEN 1 ... ELSE 0 END`，讓固定清單排序與狀態順位 View 可跨資料庫複製。
  - 本輪補齊：MySQL `STRCMP(left, right)` 會在目標非 MySQL 時轉為通用 `CASE WHEN left = right THEN 0 WHEN left < right THEN -1 ELSE 1 END`，讓字串排序/比較結果 View 可跨資料庫複製。
  - 本輪補齊：SQL Server `CHOOSE(index, value1, value2, ...)` 會在目標非 SQL Server 時轉為通用 `CASE index WHEN 1 THEN value1 ... ELSE NULL END`，讓序號轉標籤 View 可跨資料庫複製。
  - 本輪補齊：SQL Server `STUFF(expr, start, length, replacement)` 會在目標為 MySQL、PostgreSQL、SQLite 或 Oracle 時轉為 substring 串接表達式，並改用函式呼叫掃描器支援巢狀參數與保留字串常值，讓遮罩、局部替換與格式化 View 可跨資料庫複製。
  - 本輪補齊：`SUBSTR(...)` / 逗號式 `SUBSTRING(...)` 名稱轉換改用函式呼叫掃描器，可處理 `REPLACE(...)` 這類含括號與逗號的巢狀參數，並保留字串常值中的函式範例文字。
  - 本輪補齊：`LEFT(...)` / `RIGHT(...)` 複製到 Oracle 或 SQLite 時改用函式呼叫掃描器，可處理 `REPLACE(...)` 這類巢狀參數並轉為 `SUBSTR(...)`，同時保留字串常值中的函式範例文字。
  - 本輪補齊：MySQL `SUBSTRING_INDEX(expr, delimiter, 1)` 會在目標為 SQL Server、PostgreSQL、SQLite 或 Oracle 時轉為「第一個 delimiter 前段」的等價 `CASE` 表達式；`SUBSTRING_INDEX(expr, delimiter, -1)` 會在目標為 SQL Server、PostgreSQL 或 Oracle 時轉為「最後一個 delimiter 後段」的等價表達式，找不到 delimiter 時保留原字串；轉換已改用函式呼叫掃描器，可支援巢狀參數並保留字串常值中的函式範例文字。
  - 本輪補齊：MySQL/PostgreSQL/SQL Server `CONCAT_WS(separator, ...)` 複製到 Oracle 或 SQLite 時會展開為分隔符串接表達式，讓電話、代碼與複合鍵格式化 View 可跨資料庫複製。
  - 已知情境：Oracle 階層查詢、MySQL 專用 View 語法、帶 OFFSET 且缺少穩定排序的 SQL Server 轉換、無法解析的 SELECT SQL 仍會改用 table snapshot。
  - 測試覆蓋：`tests/SmokeTests.cs` 已加入 TOP / LIMIT / LIMIT OFFSET / OFFSET FETCH / FETCH FIRST / ROWNUM、日期格式與解析（含 SQL Server `CONVERT` style 23/120、`TO_DATE`、`TO_TIMESTAMP`、`STR_TO_DATE`）、目前日期/時間（含 `GETUTCDATE()` / `UTC_TIMESTAMP()` / `SYSDATE` / `SYSTIMESTAMP`）、`DATE_TRUNC` 與 Oracle `TRUNC(..., 'MM'/'YYYY'/'HH24'/'MI')` 日期截斷、`NVL2` 空值條件、`DATE` / `TRUNC` 日期截斷、`DATEDIFF` / `TIMESTAMPDIFF` / `MONTHS_BETWEEN` 年/月/日/時分秒差、`DATEADD` / `DATE_ADD` / `DATE_SUB` / `ADD_MONTHS` 年/月/日/時加減、`EOMONTH` / `LAST_DAY` 月末日期、`DATEFROMPARTS` 日期組裝與 `YEAR` / `MONTH` / `DAY` / `HOUR` / `MINUTE` / `SECOND` / `DATEPART` / `DATE_PART` / `EXTRACT` 日期部分函式、`IF` / `IIF` / `DECODE` 條件函式、`CEIL` / `CEILING` 數值進位、`MOD` 取餘數、`POW` / `POWER` 次方、`GREATEST` / `LEAST` 最大/最小值比較、`TRUE` / `FALSE` 布林常值、PostgreSQL `欄位::型別` cast operator、SQL Server `TRY_CAST` / `TRY_CONVERT`、`NULLS FIRST/LAST` 空值排序、`RAND` / `RANDOM` / `DBMS_RANDOM.VALUE` 隨機數、PostgreSQL `ILIKE`、`REGEXP_LIKE` / PostgreSQL `~` 正規表示式比對、字串聚合、`CONCAT` 與 `||` 字串串接、`LEN` / `LENGTH` / `CHAR_LENGTH` / `CHARACTER_LENGTH` 字串長度、`TRIM` / `LTRIM` / `RTRIM` 字串修剪、`SUBSTR` / `SUBSTRING` / `SUBSTRING ... FROM ... FOR` / `LEFT` / `RIGHT` / `LPAD` / `RPAD` 字串擷取與補齊、`LOCATE` / `CHARINDEX` / `INSTR` / `POSITION` 字串位置、JSON 純量讀取（含 `JSON_EXTRACT` / `JSON_UNQUOTE(JSON_EXTRACT(...))` / `JSON_VALUE` / MySQL `->>` / PostgreSQL `->>` / `#>>` 轉 PostgreSQL / MySQL / Oracle / SQL Server）、JSON 片段讀取（含 `JSON_QUERY` / MySQL `->` / PostgreSQL `->` / `#>` 轉 MySQL / PostgreSQL / SQLite）、JSON path 存在判斷（含 `JSON_EXISTS` / `JSON_CONTAINS_PATH` 轉 MySQL / PostgreSQL / SQLite / SQL Server / Oracle）、JSON 陣列長度（含 `JSON_LENGTH` / `JSON_ARRAY_LENGTH` 轉 MySQL / PostgreSQL / SQLite / SQL Server / Oracle）、JSON 物件/陣列建構（含 MySQL、PostgreSQL、SQLite、Oracle 互轉）、簡單 `JSON_TABLE` 轉 PostgreSQL / SQL Server / SQLite、`JSON_TABLE FOR ORDINALITY` 轉 PostgreSQL / SQLite 與 SQL Server 不支援原因、CTE/window 保留、`WITH RECURSIVE` 關鍵字轉換與不支援轉換原因的可重跑案例。
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
