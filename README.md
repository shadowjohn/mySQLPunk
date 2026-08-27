# mySQLPunk

[![Auto Release Gate](https://github.com/shadowjohn/mySQLPunk/actions/workflows/auto-release.yml/badge.svg)](https://github.com/shadowjohn/mySQLPunk/actions/workflows/auto-release.yml)
[![Latest release](https://img.shields.io/github/v/release/shadowjohn/mySQLPunk?display_name=tag)](https://github.com/shadowjohn/mySQLPunk/releases/latest)
[![License](https://img.shields.io/github/license/shadowjohn/mySQLPunk)](LICENSE)

> 免費開源的 Windows 多資料庫 GUI、SQL 編輯器與 DBA 工作台

mySQLPunk 是一套 Windows 資料庫管理工具（WinForms），用同一個介面連接 MySQL / MariaDB、PostgreSQL、SQL Server、SQLite / SpatiaLite、Oracle、MongoDB、Redis / Microsoft Garnet，以及 Snowflake。關聯式資料庫可瀏覽與編輯資料、撰寫 SQL、查看執行計畫、設計資料表、比較結構、匯出與備份；MongoDB 提供文件工作流程，Redis / Garnet 提供 key 瀏覽、受限查詢與五種型別的安全編輯，Snowflake 第二階段以 SQL REST API 提供瀏覽、查詢與查詢編輯器 DML／DDL。

Open-source Windows database client, SQL editor, database GUI and DBA workbench for MySQL, MariaDB, PostgreSQL, SQL Server, SQLite, SpatiaLite, Oracle, MongoDB, Redis, Microsoft Garnet and Snowflake workflows.

[下載最新版](https://github.com/shadowjohn/mySQLPunk/releases/latest) · [功能概況](#目前功能概況) · [連線安全](docs/CONNECTION_SECURITY.md) · [自動執行作業](docs/AUTOMATION.md) · [開發與貢獻](CONTRIBUTING.md) · [功能路線圖](docs/ROADMAP.md)

## 常見用途

- 在同一套 Windows GUI 管理 MySQL / MariaDB、PostgreSQL、SQL Server、SQLite / SpatiaLite、Oracle、MongoDB、Redis / Garnet 與 Snowflake 連線。
- 依連線設定 SSL/TLS 憑證驗證，或透過有 SHA256 主機金鑰固定的 SSH Tunnel 連到內網資料庫。
- 使用具自動完成、程式碼片段、查詢歷史、唯讀執行計畫與多格式匯出的 SQL 編輯器。
- 編輯資料列、設計資料表、產生 DDL / DML、搬移 Table / View、建立 ER 圖並比較兩個資料庫結構。
- 建立每日查詢、CSV / Excel / JSON 等格式匯出與 SQL 備份作業，交由 Windows Task Scheduler 執行並保留紀錄。
- 選用 OpenAI 相容 API、Ollama、LM Studio、Codex CLI、Claude Code CLI 或 Gemini CLI 作為 SQL 助理；沒有 AI 服務也能使用其他資料庫功能。

介面支援繁體中文與英文。資料庫密碼、SSH 密碼、私鑰密語與用戶端憑證密碼存在 Windows 認證管理員，不會以明文留在設定檔或自動執行作業檔。

<p align="center">
  <img src="snapshot/mySQLPunk_avatar.png" alt="看板娘：Punky 崩琦" width="260">
</p>
<p align="center"><strong>看板娘：Punky 崩琦</strong>，現在也會在「說明 > 關於」裡眨眼打招呼。</p>

作者：

- 羽山秋人 ( https://3wa.tw )
- [**NickYCLin**](https://github.com/NickYCLin) ([https://github.com/NickYCLin](https://github.com/NickYCLin))

## 最新版本

目前發版版本：`v1.0.0.19`，最新版請看 [GitHub Releases](https://github.com/shadowjohn/mySQLPunk/releases)。

目前 GitHub Release 會只提供一個 `mySQLPunk-<version>-win-x64-setup.exe`。安裝程式內含程式運作所需的 managed DLL、SQLite／SpatiaLite 原生 runtime、素材與第三方授權檔；使用者不需要另外下載 ZIP 或 manifest。完整變更請見 `CHANGELOG.md`。

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
.\scripts\package-release.ps1 -Version 1.0.0.19
```

此腳本會使用 Release 組態建置專案，再以 Inno Setup 6 將 `mySQLPunk/bin/Release` 封裝成單一 `dist/mySQLPunk-<version>-win-x64-setup.exe`。安裝內容會帶入 `LICENSE`、`THIRD_PARTY_NOTICES.md` 與可取得的 NuGet license/notice 檔，並排除不屬於程式必要 runtime 的 `sqlite3.exe`、`libreadline8.dll`、`libtermcap-0.dll`。本機打包前需先安裝 Inno Setup 6，或用 `-InnoSetupCompiler` 指定 `ISCC.exe`。

GitHub Actions 發版：

```powershell
# 1. 先確認 mySQLPunk/Properties/AssemblyInfo.cs 的 AssemblyVersion / AssemblyFileVersion
#    已更新成要發布的版本，例如 1.0.0.19。
git tag v1.0.0.19
git push origin v1.0.0.19
```

推送 `v*` tag 後，`.github/workflows/release.yml` 會在 GitHub 的 Windows runner 上還原 NuGet、用 MSBuild 編譯 Release、安裝固定版本且先驗證 SHA-256 的 Inno Setup、執行 `scripts/package-release.ps1`，並建立或更新 GitHub Release。Release 會先清除同版本舊的 ZIP／manifest 等資產，再只上傳一個 setup EXE。也可在 GitHub Actions 手動執行 `Release` workflow 並輸入版本號。Workflow 會檢查 tag / 手動輸入版本是否和 `AssemblyFileVersion` 一致，避免程式內更新檢查一直判定同一版本可更新；`scripts/New-ReleaseNotes.ps1` 會從 `CHANGELOG.md` 的對應版本產生繁體中文 `🚀 新增功能`、`🛠️ 問題修正與優化`、`📦 下載與更新`、`🛡️ 完整性與驗證` 四段說明，並寫入安裝檔的實際 SHA-256 與 Authenticode 狀態。若缺少對應版本或必要段落，發佈會直接停止，不會建立內容不完整的 Release。

日常開發由 `.github/workflows/auto-release.yml` 控制，不會每個小修改都建立 Release：`feat` 算 2 分，`fix`／`perf`／`refactor` 算 1 分，累積 5 分才發布；已有程式變更但七天未達門檻時會合併成一批發布。單一大更新可在 commit footer 加上 `Release-Now: true` 立即發布。完整規則與範例請見 [`docs/RELEASE_AUTOMATION.md`](docs/RELEASE_AUTOMATION.md)。

備註：

- Repo 根目錄有提供 `NuGet.Config`，會強制將 NuGet 還原目錄固定在本專案的 `packages/`，避免受使用者全域 NuGet 設定影響導致 `..\packages\...` 找不到。

Smoke test harness：

```powershell
.\tests\Run-SmokeTests.ps1
```

目前 70 項 smoke test 會先建置 `mySQLPunk.sln`，再編譯並執行 `tests/SmokeTests.cs`，覆蓋 `DatabaseCopyService` 的 View SQL 跨 provider 轉換（TOP / LIMIT / ROWNUM、日期、字串聚合、JSON、CTE/window 與 unsupported reason）、`GeometryWktConverter` 的 WKB/WKT 基本轉換與錯誤案例、SQLite FTS/RTree/SpatiaLite 專用 SQL builder、Table Designer 主要 DDL builder、連線 URI 匯入、MongoDB provider 基礎與文件樹／安全編輯規則、Redis RESP／URI／provider 瀏覽與 WATCH/MULTI/EXEC string／集合安全編輯的 loopback 連線流程、Snowflake SQL REST API 的 loopback HTTP 流程（bearer 驗證、202 輪詢、partition 合併、唯讀結果入口與 DML 寫入）、連線星號／色彩／批次屬性、SSL/TLS 與 SSH 安全設定、自動執行作業、管理畫面與 Windows 工作排程規格，以及 `DatabaseDumpService` / `QueryResultExportService` / `ConnectionOpenService` / `MetadataLoadService` 的非 UI service 測試。

需要只重跑單一測試群組時，可在執行測試程式前設定 `MYSQLPUNK_SMOKE_FILTER`（不分大小寫比對測試名稱）；沒有任何項目符合時會以錯誤碼結束，避免空跑誤判成功。

MySQL / MariaDB 使用者管理實機矩陣（需先啟動 Docker）：

```powershell
.\tests\Run-MySqlUserIntegrationTests.ps1
```

此測試會依序啟動 MySQL 5.6、5.7、8.0、MariaDB 10.6、10.11、11.4，實際驗證 User List、Create / Alter / Rename / Drop、SSL 與 resource limits、Table / Procedure Grant/Revoke、`SHOW GRANTS`、安全 DDL preview 與 provider SQL 失敗判定。

MySQL / MariaDB 匯出、匯入與 copy-based database rename 實機矩陣（需先啟動 Docker）：

```powershell
.\tests\Run-MySqlExportRenameIntegrationTests.ps1
```

此測試會在上述六個版本建立含主鍵、唯一鍵、一般索引、外鍵、AUTO_INCREMENT、資料表/欄位註解、utf8mb4 中文與 emoji、特殊字元、NULL、BLOB、decimal、datetime、View、Function、Procedure、Trigger 的資料庫，實際驗證串流匯出、UTF-8 without BOM、刪除後重新匯入、既有物件策略、指定物件匯出，以及保留原資料庫的 copy-based rename；同一份匯出檔也會再交給容器內建的 `mysql` / `mariadb` CLI 匯入並查回資料筆數，確認不是只有 mySQLPunk 自己能解析。

SQLite / PostgreSQL / SQL Server database rename 實機矩陣（需先啟動 Docker 以測試後兩者）：

```powershell
.\tests\Run-DatabaseRenameProviderIntegrationTests.ps1
```

此測試會實際驗證 SQLite 檔案移動後可重新開啟與讀取資料、PostgreSQL `ALTER DATABASE ... RENAME TO ...`、SQL Server `ALTER DATABASE ... MODIFY NAME`，並確認舊名稱消失、新名稱可由 provider metadata 讀取。

## 目前功能概況

| 功能 | 狀態 | 說明 |
| --- | --- | --- |
| 連線管理 | 可用 | 預設連線資訊儲存在 `setting.ini`，支援多設定檔、多層群組、拖曳、持久化星號／色彩，以及批次修改星號、群組與色彩；新增精靈可匯入 MySQL／MariaDB、PostgreSQL、SQL Server、Oracle、SQLite、MongoDB、Redis 與 Snowflake URI，解析後先開設定頁確認；資料庫／SSH／憑證密碼改存 Windows Credential Manager，設定檔只保留 credential target；四種既有網路 RDBMS provider 可設定 SSL/TLS 與 SSH Tunnel，詳見[連線安全說明](docs/CONNECTION_SECURITY.md)。 |
| MySQL | 可用 | 主要 provider，支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer。 |
| MySQL / MariaDB 使用者管理 | 可用 | 自動偵測 MySQL 5 / MySQL 8 / MariaDB；支援使用者 CRUD、密碼/Plugin/Lock/Expire/SSL/資源限制、Database/Table/View/Routine 權限編輯、SQL 預覽、`SHOW GRANTS` 與安全 DDL，並保留同名不同 Host 的獨立節點。 |
| MySQL 匯出 / 匯入 | 可用 | 可選 Table/View/Routine/Trigger，支援完整 `SHOW CREATE` 結構、批次資料、DEFINER 移除、DELIMITER、UTF-8 without BOM 與串流檔案處理；匯入可選照 SQL 執行、刪除重建、只建不存在物件、略過既有物件與資料。 |
| Database inline rename | 可用 | Database、Table、View 可用 F2 或右鍵改名；Esc 只取消編輯。MySQL 以完整 SQL 串流複製並保留舊 DB，PostgreSQL/SQL Server 使用原生 rename，SQLite 會移動檔案並更新連線路徑。 |
| PostgreSQL | 可用 | 支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；`public` 以外的 schema 會以 `schema.table` 顯示並可用於主要資料表操作，部分進階索引仍有限制。 |
| SQLite | 可用 | 支援一般 SQLite 與 SpatiaLite 載入；欄位註解以 mySQLPunk sidecar metadata table 保存。 |
| SQL Server | 可用 | 支援 metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；`dbo` 以外的 schema 會以 `schema.table` 顯示並可用於主要資料表操作。 |
| Oracle | 部分可用 | 支援 schema/table/view metadata、資料瀏覽、資料編輯、DDL、Dump、Table Designer；部分 DDL 仍受權限、語法與物件型態限制。 |
| MongoDB | 第三期可用 | 支援一般與 SRV 連線、URI 匯入、database／collection／view metadata、抽樣 schema 推斷、索引與統計資訊、分頁文件瀏覽與 JSON find 查詢；文件檢視器提供可展開文件樹、單一文件安全編輯（`_id` 鎖定＋樂觀並行比對）、文件新增與安全刪除。standalone 4.4／7.0／8.0 已通過實機矩陣；Atlas／SRV 驗證環境與 Aggregation Pipeline 仍在後續階段。 |
| Redis / Microsoft Garnet | 第三期可用 | 支援 `redis://`／`rediss://`、ACL／密碼驗證、logical db、TLS 直連、key 型別／TTL／摘要瀏覽與 pattern／type／單一 key 受限查詢；key 編輯器依型別切換，string 值、hash 欄位、list 元素（含尾端新增）、set 成員與 zset 分數都以 WATCH/MULTI/EXEC 並行保護寫入，另有 TTL 設定／移除與刪除 key。Redis 6.2、Redis 7 與 Garnet standalone 各 39 項實機矩陣通過；Cluster／Sentinel、list 元素刪除、Pub/Sub 與監控仍待補。 |
| Snowflake | 第二期可用 | 以 SQL REST API v2 直連（Programmatic Access Token 或 OAuth token），支援 SHOW DATABASES、INFORMATION_SCHEMA metadata、schema.table／view 瀏覽、欄位與列數、分頁資料檢視、SELECT／SHOW，以及查詢編輯器的單一 DML／DDL；URI 匯入與設定保存沿用既有流程。所有值以字串呈現；真實帳戶實機驗收、key-pair JWT、參數綁定、資料網格寫回與 bulk load 仍待補。 |
| SQL 查詢 | 可用 | 支援 SELECT/SHOW/EXPLAIN/DESC/WITH 類結果顯示、多格式匯出、語法格式化、查詢歷史；MySQL／MariaDB、PostgreSQL、SQL Server、Oracle、SQLite 都可產生唯讀原生執行計畫，以樹狀、原始資料、文字與可用成本統計檢視。 |
| SQL 編輯輔助 | 可用 | 自動完成會解析目前 statement 的資料表與 alias，提供欄位、`alias.column`、資料表、關鍵字與片段捷徑；metadata 依 provider/database 快取。`Ctrl+Shift+P` 可搜尋、插入、新增、刪除自訂 SQL 片段，並以 JSON 匯入／匯出。 |
| 資料表資料編輯 | 可用 | 支援新增、修改、刪除與儲存；可為每張資料表保存多組具名篩選、排序與顯示欄位設定，並從底部工具列快速切換。若沒有 Primary Key，預設更新/刪除前會顯示風險警告，也可在選項中改為唯讀開啟。 |
| 資料分析 | 可用 | 資料表右鍵可開啟欄位分析工作區；支援抽樣／全表、NULL、相異值、極值、數值平均、Top 10 比例，並可從分佈值開啟對應 WHERE 查詢。 |
| Table Designer | 部分可用 | 支援新增資料表與多 provider ALTER 預覽/儲存；既有資料表欄位改名、型別、NULL、DEFAULT、註解、MySQL 刪欄位與 SQLite 重建表已納入 smoke test，部分進階索引與 constraint 情境仍需實機驗證。 |
| 自動補註解 | 可用 | 可從遠端字典補欄位註解，支援「補空白註解」與「覆蓋註解」兩種模式；SQLite 會寫入 sidecar metadata table。 |
| 補註解進度視窗 | 可用 | 使用遮罩視窗與 CC0 貓咪跑者 GIF 顯示逐筆進度。 |
| 資料產生 | 可用 | Tables 節點可產生指定資料表的 INSERT SQL，可開到查詢視窗檢查，也可確認後逐筆直接寫入。 |
| 命令列介面 | 可用 | 支援 MySQL、PostgreSQL、SQL Server、SQLite、Oracle 的 CLI 啟動指令；需本機已安裝對應客戶端工具。 |
| Table/View 複製 | 可用 | 跨 provider 複製 Table/View；View SQL 無法安全轉換時會改用 table snapshot。 |
| SQL Dump | 可用 | 支援多 provider Table dump；SQLite 欄位註解 sidecar metadata 會隨結構匯出；各 provider 的 DDL 細節仍會依 metadata 能力不同而有差異。 |
| 匯出 / Dump / Backup service | 可用 | 查詢結果多格式匯出、SQL dump 與邏輯 SQL 備份已抽出 service，Form UI 只負責觸發、檔案對話框與狀態呈現。 |
| 自動執行作業 | 可用 | 「工具 > 自動執行作業」可建立唯讀查詢、查詢結果匯出與 SQL 備份；支援立即執行、每日 Windows 工作排程與 JSON 執行紀錄。排程使用同一個已登入的 Windows 帳號讀取 Credential Manager，不把帳密寫進作業檔。詳見 [`docs/AUTOMATION.md`](docs/AUTOMATION.md)。 |
| 連線與 metadata service | 可用 | 連線開啟、retry 判斷與 database metadata snapshot 已抽出 service，Form UI 保留 TreeView 呈現與錯誤提示。 |
| 選項中心 | 部分可用 | 已補齊主要分類頁與 `application-options.json` 保存；查詢視窗已套用記錄限制、編輯器字型/換行/Tab 空格、自動完成開關、大型 SQL 停用編輯器輔助、資料表儲存自動交易、SQL 檔案位置、匯出位置、還原差異抽樣列數、結果網格字型與列高度、日期/時間與數字格式、工具提示顯示開關、診斷記錄、自動復原草稿、索引標籤開啟偏好、HTTP 代理與進階註冊設定。 |
| 單一實例、檔案關聯與物件 URI | 可用 | 「允許重複執行 mySQLPunk」選項關掉時強制單一實例（預設允許多開）；`.sql` 檔可以用「開啟方式」直接開進查詢分頁。database、Table、View、Function、User、Event 與內建工具物件可複製 `mysqlpunk://object` URI，啟動後會沿用目前設定檔的同名連線定位；URI 不保存主機、帳號或密碼。 |
| 介面與語系 | 可用 | 圖示全面向量繪製（引擎專屬色＋形狀徽章），支援淺色／深色主題，繁中／英文可即時切換；「說明 > 關於」會播放去背的看板娘 Punky 崩琦眨眼動畫。 |
| 應用程式更新 | 可用 | GitHub Release 只發布一個 setup EXE；說明選單可手動檢查，也可在啟動時背景檢查。程式會讀取 GitHub release asset 的 SHA-256 digest，下載後先校驗再啟動安裝程式；舊版 portable ZIP 更新仍保留相容處理。 |
| Punky AI 助理 | 可用 | 預設開啟的右側聊天面板，可關閉並從「檢視」選單重開，也能和物件詳細資料暫時收合到右側圖示列；走 OpenAI 相容 API（OpenAI／Ollama 本機模型／自訂端點），可附上目前資料庫結構當上下文，回覆的 SQL 可一鍵插入查詢分頁；金鑰存 Windows 認證管理員。 |
| 資料字典 | 可用 | 資料庫節點右鍵產生整庫結構文件（欄位、索引、CREATE 語句、目錄），輸出 HTML，瀏覽器可另存 PDF。 |
| 唯讀 ER 圖 | 可用 | 五種 provider 共用 schema 快照，可顯示資料表、欄位、主鍵與外鍵關聯；支援縮放、適合視窗、中鍵平移、重新整理與完整圖面 PNG 匯出。 |
| 資料庫結構差異 | 可用 | 可從 Models 或資料庫右鍵選擇另一個已開啟的資料庫，比對資料表、欄位型別、空值、主鍵與外鍵；支援跨 provider、交換方向、重新比較及 HTML 報告。整個流程唯讀，不會執行 DDL。 |
| 查詢輔助 | 可用 | 釘選查詢結果快照對照比較、連線清單即時搜尋、F11 專注模式。功能對照與後續規劃見 `docs/ROADMAP.md`。 |

## 已知限制

- Oracle 的部分 DDL 還是會被權限、語法或物件型態擋下來；預覽會附上權限診斷 SQL 跟修復建議，但終究要看帳號實際有什麼權限。
- 沒有 Primary Key 的資料表，編輯時是拿原始值組 WHERE 條件去比對；欄位有浮點數或大文字時可能比不準。不放心的話選項裡可以改成唯讀開啟。
- XLSX 匯出要把整份結果放進記憶體；還原 SQL 備份也是整個檔一次讀進來，特別大的備份要留意。
- 樹狀清單的引擎圖示是自繪的品牌色底加白色剪影（海豚＝MySQL、大象＝PostgreSQL、圓環＝Oracle、羽毛＝SQLite、資料庫圓柱＝SQL Server），已連線顯示品牌色、未連線是灰色。剪影是自己畫的風格化版本，不是各家原廠 logo 原圖，因為商標授權不好處理、原圖縮到 16px 也不清楚。
- SQL Server 物件名稱本身帶 `.` 的話，`schema.table` 可能會切錯位置。
- 一般代理設定目前只有 HTTP 路徑有接（檢查更新、註解字典下載這些）；資料庫連線可用 SSH Tunnel，但還沒有 SOCKS5 代理。
- 安裝檔還沒上程式碼簽章，第一次下載執行可能會跳 SmartScreen 警告。
- Table Designer 的 Primary Key 跟 constraint 進階變更，還需要更多實機案例驗證。
- 資料分析預設使用前 10,000 筆樣本；切換成全表會執行 COUNT／DISTINCT／GROUP BY，對大型資料表可能耗時。BLOB、JSON、geometry 等型別會略過資料庫不支援的極值或分佈統計。
- ER 圖與結構差異報告目前都只讀取 metadata，不會回寫資料庫；ER 圖每張卡片先顯示前 16 個欄位，還沒有拖曳位置保存、關聯篩選或從模型回寫資料庫，結構差異也還不會產生同步 DDL。
- MongoDB 查詢分頁維持唯讀，寫入只能走文件檢視器的安全編輯、新增與刪除；schema 由前 100 筆文件推斷。尚未提供 Aggregation Pipeline 視覺設計、SSH Tunnel 或 Atlas 專用驗證；standalone 4.4／7.0／8.0 實機矩陣已通過，Atlas／SRV 與帳號驗證環境仍待驗收。
- Redis / Garnet 目前只支援 standalone 直連；寫入僅限 key 編輯器（string 值、hash 欄位、list 元素編輯與尾端新增、set 成員、zset 分數、TTL 與刪除 key），list 元素刪除因 Redis 無依索引刪除命令暫不提供，值含非 UTF-8 位元組時不開放編輯。SCAN 的 `COUNT` 只是提示，程式會在單次 traversal 內去重，但資料同時變更時跨頁仍可能變動。Cluster、Sentinel、Pub/Sub 與監控尚未提供；Redis 6.2／7 與 Garnet standalone 實機矩陣可用 `tests/Run-RedisLiveMatrixTests.ps1` 重跑。
- Snowflake 結果集入口僅接受 SELECT／SHOW／DESC／EXPLAIN／WITH，DML／DDL 必須從查詢編輯器執行；以 SQL API JSON 讀取時所有欄位值以字串顯示，物件名稱採 schema.table 拆解（名稱內含點號者不支援）。查詢會使用連線設定的 database／warehouse／role；尚未對真實 Snowflake 帳戶實機驗收，key-pair JWT、參數綁定、資料網格寫回、暫存區與 bulk load 未提供。

各功能做完的細節紀錄在 [`docs/FEATURE_NOTES.md`](docs/FEATURE_NOTES.md)。

## 專案檔案導覽

- `mySQLPunk/Program.cs`: 程式進入點、單一實例與 .sql 檔案參數處理。
- `mySQLPunk/Form1.cs`: 主視窗、左側連線樹、右鍵選單、metadata 瀏覽、資料庫級操作。
- `mySQLPunk/AboutDialog.cs`: 自訂關於視窗，顯示版本、作者資訊與看板娘 Punky 崩琦眨眼動畫。
- `mySQLPunk/QueryForm.cs`: SQL 編輯器、查詢結果、資料表資料瀏覽與儲存。
- `mySQLPunk/lib/my_mongodb.cs`: MongoDB provider、metadata、schema 推斷、文件分頁、JSON find 查詢與單一文件安全寫回。
- `mySQLPunk/lib/MongoDocumentEditService.cs`: MongoDB 文件樹、編輯驗證與並行比對規則（純邏輯）。
- `mySQLPunk/MongoDocumentViewerForm.cs`: MongoDB 文件檢視器（文件樹＋Canonical Extended JSON 編輯）。
- `mySQLPunk/template/mongodb_add_edit.cs`: MongoDB 一般／SRV 連線設定與測試畫面。
- `mySQLPunk/lib/RedisRespClient.cs`: Redis RESP2 編碼、解析、TCP／TLS 往返與回覆大小防護。
- `mySQLPunk/lib/my_redis.cs`: Redis / Garnet provider、logical db、SCAN key 瀏覽、受限查詢與 WATCH/MULTI/EXEC 安全編輯。
- `mySQLPunk/RedisKeyEditorForm.cs`: Redis key 編輯器（string 值、TTL 與刪除）。
- `mySQLPunk/template/redis_add_edit.cs`: Redis / Garnet 連線設定與測試畫面。
- `mySQLPunk/lib/my_snowflake.cs`: Snowflake provider、metadata、分頁查詢與查詢編輯器 DML／DDL（SQL REST API）。
- `mySQLPunk/lib/SnowflakeRestClient.cs`: Snowflake SQL API v2 client：bearer 驗證、202 輪詢與 partition 讀取。
- `mySQLPunk/template/snowflake_add_edit.cs`: Snowflake 連線設定與測試畫面。
- `mySQLPunk/TableDesignerForm.cs`: 資料表設計器、欄位/索引/SQL 預覽。
- `mySQLPunk/RunnerProgressOverlay.cs`: 補註解遮罩進度視窗。
- `mySQLPunk/AnimatedRunnerProgressBar.cs`: 跑者動畫進度條控制項。
- `mySQLPunk/AutoCommentMode.cs`: 補註解模式定義。
- `mySQLPunk/OptionsForm.cs`: 選項中心。
- `mySQLPunk/ScheduledJobsForm.cs`: 查詢、匯出與備份作業的管理、編輯與執行紀錄畫面。
- `mySQLPunk/Localization.cs`: 繁中／英文語系字串。
- `mySQLPunk/entity/mySQLPunk_main.cs`: 連線設定載入／儲存、設定檔與憑證管理。
- `mySQLPunk/lib/IDatabase.cs`: database provider 介面。
- `mySQLPunk/lib/ObjectUriService.cs`: `mysqlpunk://object` URI 建立、驗證、解析與樹狀物件類型對應。
- `mySQLPunk/lib/ConnectionUriImportService.cs`: RDBMS、MongoDB 與 Redis 的連線 URI 驗證、解析與安全設定對應。
- `mySQLPunk/lib/ConnectionBatchPropertiesService.cs`: 連線星號、群組與色彩的正規化及批次更新。
- `mySQLPunk/lib/ScheduledJobService.cs`: 可攜式作業定義、唯讀 SQL 驗證、作業儲存、CLI 執行與紀錄。
- `mySQLPunk/lib/WindowsScheduledTaskService.cs`: Windows Task Scheduler 每日排程註冊與移除。
- `mySQLPunk/lib/ConnectionSecurityService.cs`: TLS 設定、SSH 主機金鑰驗證、Tunnel 生命週期與安全連線包裝。
- `mySQLPunk/lib/my_mysql.cs`: MySQL provider。
- `mySQLPunk/lib/my_postgresql.cs`: PostgreSQL provider。
- `mySQLPunk/lib/my_sqlite.cs`: SQLite provider。
- `mySQLPunk/lib/my_mssql.cs`: SQL Server provider。
- `mySQLPunk/lib/my_oracle.cs`: Oracle provider。
- `mySQLPunk/lib/DatabaseCopyService.cs`: Table/View 跨 provider 複製服務。
- `mySQLPunk/image/progress_runner.gif`: 補註解進度視窗的 CC0 跑者動畫。
- `mySQLPunk/image/progress_runner_LICENSE.txt`: 跑者動畫素材來源與授權資訊。
- `mySQLPunk/image/mySQLPunk_avatar_wink.gif`: 關於視窗使用的透明背景看板娘 Punky 崩琦眨眼動畫。
- `snapshot/mySQLPunk_avatar.png`: README 使用的看板娘 Punky 崩琦靜態圖。
- `snapshot/mySQLPunk_avatar_wink.mp4`: 看板娘眨眼動畫原始素材，runtime 使用去背後的 GIF。
- `mySQLPunk/lib/`: 其餘 service 層（匯出、備份、複製、更新檢查、憑證等）。
- `tests/`: smoke test（`Run-SmokeTests.ps1`）與 Docker 實機整合測試。
- `scripts/`: 打包（`package-release.ps1`）與發版說明（`New-ReleaseNotes.ps1`）腳本。
- `docs/FEATURE_NOTES.md`: 功能完成紀錄。
- `docs/AUTOMATION.md`: 自動執行作業的操作、安全設計、檔案位置與命令列說明。
- `docs/CONNECTION_SECURITY.md`: 各 provider 的 SSL/TLS、SSH Tunnel、憑證與祕密保存方式。

## 協作規範

開發流程、測試、commit、發版、文件維護的規範都在 [`CONTRIBUTING.md`](CONTRIBUTING.md)，動手前先看一下。幾個最容易踩到的：

- 動手前先 `git pull --rebase origin master`；建置失敗不 commit、不 push。
- 行為有變就要跑 `tests/Run-SmokeTests.ps1`，全過才能 commit。
- 改了什麼就把 `CHANGELOG.md` 的 `[Unreleased]` 段補上（🚀／🛠️ 那兩個標題是發版腳本硬性檢查的，不能改）。
- 修掉 README「已知限制」裡的項目時，記得把那條拿掉，細節補到 `docs/FEATURE_NOTES.md`。
