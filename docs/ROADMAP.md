# 功能路線圖（對照 Navicat Premium / Navicat 17）

本表以 2026-08-25 讀取的 [Navicat Premium 功能頁](https://www.navicat.com/cht/products/navicat-premium)與 [Navicat 17 Highlights](https://www.navicat.com/en/navicat-17-highlights)為範圍。使用者已指定「網址裡面提到的功能都要有」，因此舊版標成「不做」的 BI、協作、MongoDB、Redis、Snowflake 與 Linux ARM 全部改回長期排程，不再從範圍刪除。

狀態標記：✅ 核心功能已具備｜🆕 本輪完成｜🟡 已有部分能力｜📋 尚未實作

## Navicat 17 亮點對照

| Navicat 功能 | 狀態 | mySQLPunk 現況與完成條件 |
| --- | --- | --- |
| AI 助理、多聊天室、附加 schema、跨模型答案比較 | ✅ | Punky 停靠聊天面板支援互相隔離且只存於記憶體的多聊天室、schema 上下文、多家 API／本機 CLI、CLI 帳號偵測卡片與模型切換；同一問題也能帶著最近上下文與 schema 快照交給左右兩個模型，於獨立視窗並排比較，不會污染原聊天室。 |
| 詢問 AI：可自訂／釘選動作 | 🆕 | 查詢工具列除了內建解釋／最佳化／修正錯誤，也能新增自己的提示動作並選擇是否釘選；名稱、提示與釘選狀態保存於本機 JSON，執行時只建立含目前 SQL 的草稿，不會自動送出或執行。 |
| 解釋／最佳化／格式化／跨資料庫轉換 SQL，差異並排確認 | 🆕 | 「詢問 AI」集中提供解釋、最佳化、只調整排版的格式化，以及轉成 MySQL／MariaDB、PostgreSQL、SQL Server、Oracle 或 SQLite 的語法轉換；會停用目前 provider，所有改寫結果都先逐行比較並勾選變更區段，確認後才套用。 |
| AI 修正 SQL 錯誤 | 🆕 | 查詢失敗後可一鍵把當次 SQL、provider、資料庫與已遮蔽敏感值的錯誤帶進 AI 草稿；回覆 SQL 會先並排比較，確認後才套回當次選取範圍或全文，編輯器中途有變更則拒絕覆寫。 |
| 同一工作區多模型、Function／Procedure 物件 | 🟡 | Windows ER 模型工作區可在同一個模型檔（.punkmodel）保存多張圖表，每張圖各自挑選資料表與版面；待補 Function／Procedure 物件。 |
| 圖表樣式、圖層、鎖定、群組、自動排列、連接線重導 | 🟡 | Windows 版已完成拖曳移動、依外鍵分層的自動排列、群組（顏色、顯示／隱藏、鎖定）與 SVG 向量匯出；連接線手動重導待補。 |
| 模型與資料庫雙向比較／同步 | ✅ | Windows ER 模型工作區可把資料庫結構擷取進模型（之後再擷取會先列出差異並確認），在模型中離線新增／修改／刪除資料表、欄位與外鍵，再以「同步模型到資料庫」比較並逐句審核執行（破壞性語句預設不勾、需輸入資料庫名稱）；模型結構隨 .punkmodel 保存。Linux／macOS 版待補。 |
| 關聯式／維度／Data Vault 2.0 模型 | 📋 | 納入模型工作區第三階段。 |
| 資料字典範本、個人化、PDF、自動化、郵件、模型字典 | 🟡 | Windows 版已有完整／精簡／欄位總表三種範本，可自訂標題、作者、主色、章節與資料表篩選，並可設為自動執行作業定期輸出、以郵件附件寄出；PDF 仍由瀏覽器「列印 > 另存 PDF」輸出，待補直接 PDF 與模型字典。 |
| 資料分析：型別、格式、分佈、統計與互動探索 | 🆕 | 資料表右鍵「資料分析」；五種既有 provider 共用，含抽樣／全表、NULL、相異值、極值、平均、Top 10 比例與值鑽取查詢。待補格式異常偵測與更多圖表。 |
| Query Explain：視覺／原始資料／文字／統計計畫與高成本標示 | ✅ | 五種既有 provider 都有唯讀原生計畫、樹狀節點、屬性與文字計畫；有成本資料時會標示相對高成本節點。Linux／macOS 預覽版也提供 MySQL／MariaDB、PostgreSQL、SQL Server、SQLite 的同款計畫視窗。 |
| 釘選查詢結果（SQL、耗時、不可變快照） | ✅ | 結果快照分頁可比較並可中鍵／右鍵關閉。 |
| Table Profile：多組篩選／排序／欄顯示設定 | ✅ | 每張資料表可保存多組具名設定，從資料工具列快速切換；篩選、排序、欄顯示與目前選擇會寫入本機 JSON，五種既有 provider 都會使用對應分頁語法。 |
| 物件 URI 分享與直接定位 | ✅ | database 與支援物件可複製 `mysqlpunk://object` URI；啟動時會嚴格驗證參數、沿用目前設定檔的同名連線、載入 metadata 並定位物件，URI 不包含主機或帳密。 |
| 連線精靈、進階篩選／搜尋、URI 連線 | ✅ | 連線精靈支援引擎搜尋、名稱／群組即時搜尋，以及 MySQL／MariaDB、PostgreSQL、SQL Server、Oracle、SQLite、MongoDB 與 Redis URI 匯入；解析後先開啟原生設定頁供確認。 |
| 集中管理多連線、批次操作、星號、顏色、群組 | ✅ | 支援多設定檔、多層群組、拖曳、持久化星號與色彩；可從工具選單或連線右鍵勾選多筆，一次加／移星號、移動群組或套用色彩。 |
| BI 圖表互連 | ✅ | Windows 版資料庫右鍵「BI 儀表板...」：點選長條、折線點、圓餅扇形或表格列即篩選所有含同名欄位的其他圖表（可跨資料集），多個篩選以 AND 組合、再點一次取消，每張圖可關閉跨篩選。 |
| BI 自訂運算式 | ✅ | 資料集計算欄位與圖表篩選運算式：`[欄位]` 參照、四則與比較、AND／OR／NOT、NULL 傳遞，以及 IF、ROUND、COALESCE、CONCAT、LEFT、YEAR、MONTH 等 19 個函式；解析錯誤指出位置，逐列失敗指出列號。 |
| BI 連接 MongoDB／Snowflake | 🟡 | MongoDB 資料集使用唯讀 JSON find 或 aggregation pipeline（拒絕 $out／$merge），已在 MongoDB 7 實機驗證；Snowflake 走同一條唯讀 SQL 路徑，待真實帳戶實機驗收。 |
| MongoDB Aggregation Pipeline 視覺設計 | ✅ | collection 右鍵「Aggregation Pipeline...」可從 16 種唯讀 stage 範本新增、調整順序、停用、逐 stage 編輯 JSON 並即時檢查語法，預覽「到此 stage 為止」的前 20／100／500 筆輸出；$out／$merge（含巢狀）一律拒絕。可匯入既有 pipeline、複製 mongosh 語法或送到查詢視窗，查詢視窗也支援含 `pipeline` 陣列的 aggregation JSON。 |
| 專注模式 | ✅ | F11／檢視選單可隱藏工具列、導覽與資訊窗格。 |
| Snowflake | 🟡 | 第二期完成：SQL REST API 直連（PAT／OAuth token）、SHOW DATABASES 與 INFORMATION_SCHEMA metadata、schema.table 瀏覽、分頁資料檢視、SELECT／SHOW，以及查詢編輯器單一 DML／DDL；待補實機驗收、key-pair JWT、參數綁定、資料網格寫回、模型與 BI 能力。 |
| Redis standalone／Cluster／Sentinel、Microsoft Garnet | ✅ | Windows 版：RESP2 standalone 瀏覽、受限查詢、五種 key 型別安全編輯、TTL／刪除、list 依索引安全刪除、INFO 監控與 Pub/Sub；Redis Cluster（CRC16 slot 路由、MOVED／ASK、逐 master SCAN、交易固定在 key 所在節點、分片 channel 連到 slot 所屬 master）；Sentinel（解析 master 並以 ROLE 確認、訂閱 +switch-master 主動換線、連線中斷自動重連：唯讀命令透明重送、寫入與交易不重送並明確回報）。全部通過實機驗證。 |
| Linux ARM | ✅ | 已建立 .NET 8 Core 與 Avalonia 桌面預覽版；CI／Release 會在 `ubuntu-24.04` x64 與 `ubuntu-24.04-arm` ARM64 原生 runner 分別建立 self-contained 安裝壓縮檔，並完成安裝、Xvfb UI 啟動、安全更新、rollback 與解除安裝。跨平台 SQL Server 的 provider 實機 round-trip 保留在支援其容器映像的 Linux x64 runner。 |

## Navicat Premium 功能頁對照

| Navicat 功能 | 狀態 | mySQLPunk 現況與完成條件 |
| --- | --- | --- |
| 主要視窗、樹狀導覽、物件清單、分頁 | ✅ | Windows 完整版已具備可停靠／浮動分頁、多連線樹與物件清單；Linux / macOS 預覽版的 metadata 樹也可依 schema／名稱即時搜尋並依 Table／View 篩選。 |
| 物件設計器 | 🟡 | 五種 provider 已能建表與主要 ALTER；進階 constraint／索引仍需更多實機矩陣。 |
| RDBMS 資料編輯器（網格） | ✅ | Windows 完整版具備分頁瀏覽、篩選、排序、欄顯示、寫回、無主鍵安全模式與多格式匯出；Linux / macOS 預覽版已補 Primary Key 穩定分頁、metadata 白名單參數化篩選與欄位排序、依連線與 Table 安全保存的欄位顯示控制、安全寫回，以及保留目前篩選／排序與可見欄位的 CSV／TSV／JSON 本頁匯出。 |
| MongoDB 資料編輯器（網格／樹／JSON） | 🆕 | 文件檢視器提供可展開文件樹與 Canonical Extended JSON 編輯；儲存會鎖定 `_id` 並以完整原始文件做並行比對，並支援文件新增（自動 `_id`）與安全刪除；view 與缺 `_id` 文件唯讀。待補網格內編輯。 |
| Redis 資料編輯器 | ✅ | key 編輯器依型別切換：string 值編輯、hash 欄位、list 元素編輯／刪除／尾端新增、set 成員、zset 分數都有並行衝突保護，另有 TTL 設定／移除與刪除 key。 |
| 資料分析與互動圖表 | 🆕 | 已完成欄位摘要、Top 值比例與值鑽取的第一版。 |
| 自動完成程式碼 | ✅ | 已能解析目前 statement 的 FROM／JOIN／UPDATE／INTO 來源與 alias；支援欄位、`alias.column`、資料表、關鍵字與片段捷徑，並依 provider/database 快取資料表、View 與欄位 metadata。 |
| 程式碼片段 | ✅ | `Ctrl+Shift+P` 開啟片段管理器；支援 8 組內建片段、自訂片段 CRUD、全文搜尋、`$CURSOR$` 定位、保留縮排插入，以及 JSON 匯入／匯出工作區格式。 |
| 視覺化解釋 | ✅ | MySQL／MariaDB、PostgreSQL、SQL Server、Oracle、SQLite 都有原生唯讀計畫，可查看節點樹、屬性、原始資料、文字與可用成本。 |
| 視覺查詢建構器 | 🟡 | Windows 版完成：資料庫右鍵「查詢建構器...」可把資料表加入畫布、依外鍵自動連接、拖曳欄位建立 INNER／LEFT／RIGHT／FULL 連接，設定輸出、別名、彙總、排序、分組、WHERE／HAVING 條件、DISTINCT 與筆數上限；SQL 即時產生（MySQL／PostgreSQL／SQL Server／SQLite／Oracle 各自的引號與 LIMIT／TOP／FETCH），也能把可表達的 SELECT 轉回圖形。Linux／macOS 預覽版待移植。 |
| Procedure／Function 偵錯器（中斷點、逐步、變數、呼叫堆疊） | 📋 | 依 provider 能力分階段實作，優先 PostgreSQL／SQL Server。 |
| AI 助理／詢問 AI | 🟡 | 核心聊天與 schema 上下文已完成，進階動作見上表。 |
| 資料傳輸／遷移（跨 DBMS） | ✅ | Windows 版資料庫右鍵「資料傳輸...」可選任一已展開的目標資料庫（可跨 provider），逐表選擇建立新表／附加／取代資料、目標名稱與欄位對應，每批寫入後記錄檢查點，停止或失敗後可續傳（有主鍵的表從中斷處接續），完成後以列數驗證並匯出 HTML 報告；另保留 Table／View 單一物件複製。 |
| 資料同步 | 🟡 | Windows 與 Linux／macOS 皆可逐列比較同類型資料庫（MySQL／MariaDB、PostgreSQL、SQL Server、SQLite）的同名資料表、預覽 SQL，並在目標以單一交易受控同步（相依排序、並行衝突回滾、刪除需確認）；待補大表串流比較與跨類型資料庫。 |
| 結構同步 | 🟡 | 兩庫唯讀結構差異報告已完成，可跨 provider 比對 Table、Column、PK 與 FK 並匯出 HTML；Linux／macOS 預覽版另含索引與 FK 規則比對，並可為同類型資料庫產生同步 DDL 預覽（破壞性變更預設註解）；Windows 版也能依欄位／主鍵／外鍵快照產生同步 SQL 預覽；Windows 與 Linux／macOS 都可逐項勾選並在目標受控執行（交易回滾、破壞性變更輸入名稱確認、完成後自動重比）。 |
| 模型 | 🟡 | Windows ER 模型工作區：拖曳編排、自動排列、群組上色／隱藏／鎖定、多張圖表存成 .punkmodel、PNG／SVG 匯出，並可把結構存進模型離線編輯後與資料庫雙向比較／同步；待補 Function／Procedure 物件、連接線重導、維度／Data Vault 模型與 Linux／macOS 版。 |
| BI | 🟡 | Windows 版 BI 儀表板已完成：唯讀查詢資料集、計算欄位、長條／折線／圓餅／數字卡／表格、本機彙總、跨圖表篩選、.punkbi 存讀、PNG／HTML（內嵌 SVG）匯出，並可由自動執行作業排程輸出 HTML 報表、以郵件寄出；待補 Linux／macOS 版。 |
| 匯入／匯出（Excel、Access、CSV、ODBC 等） | 🟡 | MySQL SQL 匯入／匯出完整，查詢結果有常用格式；待補五種 provider 精靈對等化、Access／ODBC。 |
| 資料字典 | 🟡 | HTML 核心已完成（Windows 五種 provider，含三種範本、個人化、篩選、排程輸出與郵件附件；Linux／macOS 預覽版四種 provider 含索引／外鍵／註解），直接 PDF 待補。 |
| 資料產生器（規則、約束、參照完整性、大量資料） | 🟡 | Windows 與 Linux／macOS 皆已完成多表規則編輯（自動、預設、NULL、固定、序列、範圍、清單、樣式、字典、NULL 比例、seed）、依外鍵順序挑選真實父列、主鍵／唯一值不重複，並以單一交易寫入（每表 10 萬、每次 20 萬列）；自訂字典支援權重、內建字典（姓氏、名字、縣市、英文姓名、訂單狀態）與 CSV 匯入，兩個平台共用檔案格式。待補跨欄位條件與 CHECK 約束推論。 |
| 備份／還原與原生工具介面 | 🟡 | 已有邏輯 SQL 備份、隔離還原、差異與完整性排程；Windows 版另有原生備份／還原：SQL Server BACKUP／RESTORE（COPY_ONLY、CHECKSUM、驗證、還原為新資料庫）、PostgreSQL pg_dump／pg_restore 與 MongoDB mongodump／mongorestore。Oracle Data Pump 待補（需 Oracle 實機環境）。 |
| 自動執行：查詢、匯入／匯出、傳輸、通知郵件 | ✅ | Windows 版支援查詢、匯出、備份、CSV 匯入與跨庫傳輸（檢查點續傳）作業，失敗重試（匯入寫入部分資料後不重試）、Webhook 與郵件通知（SMTP 密碼存 Windows 認證管理員）、立即執行、每天／每週指定星期／每 N 小時／登入時的 Windows 工作排程與 JSON 紀錄。 |
| MongoDB 結構描述分析器 | ✅ | collection 右鍵「結構描述分析...」可抽樣 100～100,000 筆（前 N 筆或 `$sample` 隨機），展開巢狀文件與陣列路徑，統計出現率、型別分佈、NULL、數值／字串長度／日期／陣列長度範圍、常見值與 1.5×IQR 極端值（附 `_id`），並標出混合型別、數字存成字串、稀疏欄位、只差大小寫的欄位名稱、空字串與全為 NULL 等異常。 |
| Redis Pub/Sub | 🆕 | 停靠式訊息工作區可用 channel、pattern 或 Redis 7 分片 channel（SSUBSCRIBE／SPUBLISH）訂閱與發布、查看最近 1,000 筆訊息；接收使用專線，連線中斷（含 Sentinel 容錯切換）時自動重新訂閱最多 5 次。 |
| 協同合作：同步連線、查詢、pipeline、片段、模型、BI、群組 | 📋 | 先做本機可匯出／匯入的工作區格式與 Git 版控，再補可自架同步服務與權限。 |
| SSH tunnel、SSL/TLS | ✅ | 四種網路 provider 已有共用安全設定 UI、憑證驗證、SSH SHA256 主機金鑰固定與隧道生命週期；Linux／macOS 預覽版也具備 provider 原生 TLS 模式、憑證檔案與指紋固定的 SSH Tunnel。SQLite 為本機檔案，不適用網路層設定。 |
| PAM／LDAP／Kerberos／MFA／SSO | 📋 | 依 provider 驗證能力分階段加入，不保存明文祕密。 |
| 深色模式／平台原生設計 | ✅ | Windows 原生 WinForms、淺／深色主題與 DPI 向量圖示已完成。 |
| 跨平台授權／Windows、macOS、Linux | 🟡 | 商業授權本身不適用開源專案；Avalonia 跨平台版已可使用 MySQL / MariaDB、PostgreSQL、SQL Server、SQLite 的連線、metadata、具外部修改衝突保護與 Linux／macOS `.sql` 關聯的 SQL 文件工作流程、CSV / TSV / JSON 結果匯出、Primary Key 穩定分頁、metadata 白名單參數化篩選與欄位排序、Table 欄位顯示控制，以及常用 scalar、provider-aware integer range、SQLite NUMERIC／temporal／UUID／GUID、single／double 浮點無聲失真防護、MySQL／MariaDB mutation warning rollback、MySQL／MariaDB／SQL Server 固定長度 binary 防護與 SQL Server collation-aware 字串無損寫入、MySQL／MariaDB BIT／ENUM／SET／完整範圍 TIME／DATE／DATETIME／TIMESTAMP／UUID／INET4／INET6、PostgreSQL scalar temporal／bit string／timetz／含 typemod 無損驗證的 interval／pg_lsn／oid／xid／cid／xid8／tsvector／tsquery、1 MiB 內 binary／JSON／XML、PostgreSQL 網路位址與 SQL Server scalar temporal／legacy LOB 欄位的安全 Table 資料編輯，並可選擇以 Linux Secret Service 或 macOS Keychain 保存密碼；連線可由 URI 安全匯入，TLS 採 provider 原生模式並支援 CA／伺服器憑證與 PEM 客戶端憑證檔案（fail closed），也可透過主機金鑰指紋固定的 SSH Tunnel 連線；查詢工具列可取得四種 provider 的唯讀執行計畫，物件面板可匯出整庫 HTML 資料字典並與其他連線做唯讀結構比較、產生同步 DDL 預覽。Linux x64／ARM64 使用 self-contained tar 安裝包，macOS Intel／Apple Silicon 使用 `.app.zip`，並可依 RID 安全檢查、下載及驗證最新 Release；Linux 與 macOS 都已完成交易式套用、啟動健康檢查與 rollback，待補 macOS Developer ID/notarization、其餘 provider 與進階功能。 |

> 最新安全進度：MySQL 8、MariaDB 11.4 與 PostgreSQL 16 的一般字串已使用 byte-exact 原值比對，可在大小寫／重音不敏感 collation 下支撐 optimistic concurrency 衝突；PostgreSQL `citext` 可安全載入與編輯，保留原始格式的 `json` 也會逐 byte 攔截外部改動，`json[]`、`xml[]` 等無 element equality 的陣列則改用 canonical text UTF-8 bytes 保留安全編輯與衝突防護。Linux X11 已實際操作驗證，macOS 由原生 Intel／Apple Silicon CI 持續建置與檢查 app archive。

> 跨平台 SQL 執行安全：Linux / macOS 編輯器若有非空白選取範圍，只送出該段 SQL；沒有選取或只選到空白時才執行全文，避免同一文件中未反白的 DDL／DML 被意外執行。

> 跨平台查詢記錄：成功 SQL 只保留於本次程式執行期間，最多 50 筆／合計 2 MiB；載回編輯器不自動執行，可手動清除且不持久化可能含敏感 literal 的內容。

## Provider 與服務覆蓋

| 範圍 | 狀態 | 說明 |
| --- | --- | --- |
| MySQL／MariaDB | ✅ | 共用 MySQL provider，已有實機版本矩陣。 |
| PostgreSQL、SQL Server、Oracle、SQLite | 🟡 | 核心 metadata／查詢／編輯／DDL／備份可用，進階功能持續對等化。 |
| MongoDB | 🟡 | 第三期完成：連線、metadata、JSON find 查詢、文件樹、安全編輯與文件新增／刪除都已具備；standalone 4.4／7.0／8.0 實機矩陣通過。待補 Atlas／SRV 驗證環境矩陣；Aggregation Pipeline 設計器與查詢視窗 pipeline 格式已完成。 |
| Redis／Garnet | 🟡 | standalone、Cluster 與 Sentinel 皆已完成（URI、ACL／密碼、TLS、logical db、key 瀏覽、受限查詢、五種型別安全編輯、INFO 監控、Pub/Sub 含分片 channel、Sentinel 自動換線）。Windows 實機矩陣 Redis 6.2（53 項）、Redis 7.4（55 項）通過；Garnet 待重跑新增案例，Linux／macOS 版待補。 |
| Snowflake | 🟡 | 第二期 provider 完成（SQL REST API、PAT／OAuth、metadata、分頁瀏覽、SELECT／SHOW 與查詢編輯器單一 DML／DDL）；真實帳戶實機矩陣、key-pair JWT、參數綁定、網格寫回與 bulk load 待補。 |
| AWS、Microsoft Azure、Google Cloud、Oracle Cloud、MongoDB Atlas、Redis Enterprise Cloud、Alibaba Cloud、Tencent Cloud、Huawei Cloud | 🟡 | RDBMS、MongoDB 與 Redis 可先用標準主機連線；待補各家 IAM／SSO／MFA 與雲端專用驗證。 |
| OceanBase、PingCAP／TiDB、Dameng、Fujitsu、Kingbase、HighGo | 🟡 | TiDB 8.5 與 OceanBase CE 4.4（MySQL 模式）已加入 Docker 實機矩陣：連線、metadata、結構（註解、索引、外鍵）、安全編輯與樂觀並行衝突、結構同步、資料同步與資料產生器都通過；執行計畫會辨識 TiDB（改送 `tidb_json`）與 OceanBase 的 JSON 結構（Windows 與 Linux／macOS 皆支援）。已知差異：兩者都解析但忽略索引 DESC；OceanBase 帳號格式為 `user@tenant`。Dameng、Kingbase、HighGo、Fujitsu 需要專用驅動或授權映像，待建立矩陣。 |

## 接續順序

1. ✅ 唯讀 ER 圖與兩庫結構差異報告第一版已完成。
2. ✅ Windows 自動執行＋查詢／匯出／備份作業與記錄第一版已完成。
3. ✅ SSH tunnel＋SSL/TLS 選項 UI、憑證驗證與排程共用連線流程已完成。
4. ✅ RDBMS、MongoDB 與 Redis 的連線 URI 匯入及設定頁確認流程已完成。
5. ✅ 連線星號、持久化色彩與批次屬性操作已完成。
6. 🟡 MongoDB 第一～三期、Redis／Garnet standalone（安全編輯、監控與 Pub/Sub）、Snowflake 第二期（SQL REST API 查詢＋查詢編輯器 DML／DDL）已完成。下一步候選：Snowflake 實機驗收、key-pair JWT、參數綁定與網格寫回，MongoDB Atlas／SRV 驗證矩陣與 Aggregation Pipeline，Redis Cluster／Sentinel，或回頭補模型／BI 路線。
7. 🟡 Linux / macOS 跨平台第二階段進行中：獨立 Core、Avalonia UI、四種 RDBMS workflow、系統密碼庫、結果安全匯出、Table optimistic concurrency 編輯、provider-aware integer range、SQLite NUMERIC／temporal／UUID／GUID、single／double IEEE 754 無聲失真防護、MySQL／MariaDB mutation warning rollback、MySQL／MariaDB／SQL Server 固定長度 binary 防護與 SQL Server collation-aware 字串無損寫入、MySQL／MariaDB BIT／ENUM／SET／完整範圍 TIME／YEAR／DATE／DATETIME／TIMESTAMP／8 種 OGC spatial、MariaDB UUID／INET4／INET6、三種 provider 的無損高精度 DECIMAL／NUMERIC、PostgreSQL scalar temporal／bit string／timetz／interval／pg_lsn／oid／xid／cid／xid8／tsvector／tsquery／range／multirange／array／geometric／jsonpath／snapshot／hstore／ltree／reg*／enum／composite／extension UDT／domain、SQL Server geometry／geography／hierarchyid／alias type／sql_variant／scalar temporal、1 MiB 內 binary hex／JSON／XML、PostgreSQL 網路位址、SQL Server legacy LOB 編輯與 200 列穩定分頁、四架構 self-contained CI／Release 資產、連線 URI 安全匯入、provider 原生 TLS 模式＋CA／客戶端憑證檔案與指紋固定的 SSH Tunnel、四種 provider 的唯讀執行計畫視窗、HTML 資料字典匯出、唯讀結構比較與同步 DDL 預覽，以及依 RID 與 sidecar 完成串流 SHA-256 的安全更新下載已完成；Linux x64／ARM64 安裝、Xvfb 啟動、安全更新、rollback 與解除安裝都在同架構原生 runner 驗證，macOS Intel／Apple Silicon 也各自驗證 ZIP 安全界限、plist、架構、codesign、交易式更新與實際 app 啟動。下一步是其餘可由實機矩陣驗證的進階型別；macOS Developer ID/notarization 仍等待發版環境提供 Apple 憑證。
