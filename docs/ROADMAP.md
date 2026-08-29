# 功能路線圖（對照 Navicat Premium / Navicat 17）

本表以 2026-08-25 讀取的 [Navicat Premium 功能頁](https://www.navicat.com/cht/products/navicat-premium)與 [Navicat 17 Highlights](https://www.navicat.com/en/navicat-17-highlights)為範圍。使用者已指定「網址裡面提到的功能都要有」，因此舊版標成「不做」的 BI、協作、MongoDB、Redis、Snowflake 與 Linux ARM 全部改回長期排程，不再從範圍刪除。

狀態標記：✅ 核心功能已具備｜🆕 本輪完成｜🟡 已有部分能力｜📋 尚未實作

## Navicat 17 亮點對照

| Navicat 功能 | 狀態 | mySQLPunk 現況與完成條件 |
| --- | --- | --- |
| AI 助理、多聊天室、附加 schema、跨模型答案比較 | 🟡 | 已有 Punky 停靠聊天面板、schema 上下文、多家 API／本機 CLI、CLI 帳號偵測卡片與模型切換；待補多聊天室與並排比較。 |
| 詢問 AI：可自訂／釘選動作 | 🆕 | 查詢工具列除了內建解釋／最佳化／修正錯誤，也能新增自己的提示動作並選擇是否釘選；名稱、提示與釘選狀態保存於本機 JSON，執行時只建立含目前 SQL 的草稿，不會自動送出或執行。 |
| 解釋／最佳化／格式化／跨資料庫轉換 SQL，差異並排確認 | 🆕 | 「詢問 AI」集中提供解釋、最佳化、只調整排版的格式化，以及轉成 MySQL／MariaDB、PostgreSQL、SQL Server、Oracle 或 SQLite 的語法轉換；會停用目前 provider，所有改寫結果都先逐行比較並勾選變更區段，確認後才套用。 |
| AI 修正 SQL 錯誤 | 🆕 | 查詢失敗後可一鍵把當次 SQL、provider、資料庫與已遮蔽敏感值的錯誤帶進 AI 草稿；回覆 SQL 會先並排比較，確認後才套回當次選取範圍或全文，編輯器中途有變更則拒絕覆寫。 |
| 同一工作區多模型、Function／Procedure 物件 | 🟡 | 唯讀 ER 圖第一版已完成，可顯示資料表、欄位、主鍵與外鍵；待擴充多模型工作區與 routine 物件。 |
| 圖表樣式、圖層、鎖定、群組、自動排列、連接線重導 | 📋 | 納入模型工作區第二階段。 |
| 模型與資料庫雙向比較／同步 | 🟡 | 兩個已連線資料庫的唯讀結構差異報告已完成；待補 DB→模型、模型→DB、DDL 預覽與逐項審核。 |
| 關聯式／維度／Data Vault 2.0 模型 | 📋 | 納入模型工作區第三階段。 |
| 資料字典範本、個人化、PDF、自動化、郵件、模型字典 | 🟡 | 已能輸出五種 provider 的整庫 HTML 並由瀏覽器另存 PDF；待補範本、直接 PDF、排程、郵件與模型來源。 |
| 資料分析：型別、格式、分佈、統計與互動探索 | 🆕 | 資料表右鍵「資料分析」；五種既有 provider 共用，含抽樣／全表、NULL、相異值、極值、平均、Top 10 比例與值鑽取查詢。待補格式異常偵測與更多圖表。 |
| Query Explain：視覺／原始資料／文字／統計計畫與高成本標示 | ✅ | 五種既有 provider 都有唯讀原生計畫、樹狀節點、屬性與文字計畫；有成本資料時會標示相對高成本節點。 |
| 釘選查詢結果（SQL、耗時、不可變快照） | ✅ | 結果快照分頁可比較並可中鍵／右鍵關閉。 |
| Table Profile：多組篩選／排序／欄顯示設定 | ✅ | 每張資料表可保存多組具名設定，從資料工具列快速切換；篩選、排序、欄顯示與目前選擇會寫入本機 JSON，五種既有 provider 都會使用對應分頁語法。 |
| 物件 URI 分享與直接定位 | ✅ | database 與支援物件可複製 `mysqlpunk://object` URI；啟動時會嚴格驗證參數、沿用目前設定檔的同名連線、載入 metadata 並定位物件，URI 不包含主機或帳密。 |
| 連線精靈、進階篩選／搜尋、URI 連線 | ✅ | 連線精靈支援引擎搜尋、名稱／群組即時搜尋，以及 MySQL／MariaDB、PostgreSQL、SQL Server、Oracle、SQLite、MongoDB 與 Redis URI 匯入；解析後先開啟原生設定頁供確認。 |
| 集中管理多連線、批次操作、星號、顏色、群組 | ✅ | 支援多設定檔、多層群組、拖曳、持久化星號與色彩；可從工具選單或連線右鍵勾選多筆，一次加／移星號、移動群組或套用色彩。 |
| BI 圖表互連 | 📋 | 現有 BI 只有物件分佈／列數排名資料表；需新增儀表板與同來源聯動篩選。 |
| BI 自訂運算式 | 📋 | 納入 BI 運算式引擎。 |
| BI 連接 MongoDB／Snowflake | 📋 | 等對應 provider 與 BI 基礎儀表板完成。 |
| MongoDB Aggregation Pipeline 視覺設計 | 📋 | MongoDB provider 後續階段：拖放 stage、逐步預覽與結果驗證。 |
| 專注模式 | ✅ | F11／檢視選單可隱藏工具列、導覽與資訊窗格。 |
| Snowflake | 🟡 | 第二期完成：SQL REST API 直連（PAT／OAuth token）、SHOW DATABASES 與 INFORMATION_SCHEMA metadata、schema.table 瀏覽、分頁資料檢視、SELECT／SHOW，以及查詢編輯器單一 DML／DDL；待補實機驗收、key-pair JWT、參數綁定、資料網格寫回、模型與 BI 能力。 |
| Redis standalone／Cluster／Sentinel、Microsoft Garnet | 🟡 | RESP2 standalone 第三期完成：瀏覽、受限查詢，加上 key 編輯器的 string 與 hash／list／set／zset 安全編輯（WATCH/MULTI/EXEC）、TTL 與刪除；Redis 6.2／7 與 Garnet 各 39 項實機矩陣通過。待補 Cluster、Sentinel、list 元素刪除、監控與 Pub/Sub。 |
| Linux ARM | 🟡 | 已建立 .NET 8 Core 與 Avalonia 桌面預覽版；Linux x64 已完成 UI 實際操作，CI／Release 會建立 self-contained `linux-x64` 與 `linux-arm64` 安裝壓縮檔，待 ARM64 實機啟動驗收。跨平台 SQL Server 已使用 Linux 容器完成 provider 實機 round-trip。 |

## Navicat Premium 功能頁對照

| Navicat 功能 | 狀態 | mySQLPunk 現況與完成條件 |
| --- | --- | --- |
| 主要視窗、樹狀導覽、物件清單、分頁 | ✅ | 已具備可停靠／浮動分頁、多連線樹與物件清單。 |
| 物件設計器 | 🟡 | 五種 provider 已能建表與主要 ALTER；進階 constraint／索引仍需更多實機矩陣。 |
| RDBMS 資料編輯器（網格） | ✅ | 分頁瀏覽、篩選、排序、欄顯示、寫回、無主鍵安全模式、多格式匯出。 |
| MongoDB 資料編輯器（網格／樹／JSON） | 🆕 | 文件檢視器提供可展開文件樹與 Canonical Extended JSON 編輯；儲存會鎖定 `_id` 並以完整原始文件做並行比對，並支援文件新增（自動 `_id`）與安全刪除；view 與缺 `_id` 文件唯讀。待補網格內編輯。 |
| Redis 資料編輯器 | ✅ | key 編輯器依型別切換：string 值編輯、hash 欄位、list 元素／尾端新增、set 成員、zset 分數都有並行衝突保護，另有 TTL 設定／移除與刪除 key；list 元素刪除因 Redis 無對應命令留待後續。 |
| 資料分析與互動圖表 | 🆕 | 已完成欄位摘要、Top 值比例與值鑽取的第一版。 |
| 自動完成程式碼 | ✅ | 已能解析目前 statement 的 FROM／JOIN／UPDATE／INTO 來源與 alias；支援欄位、`alias.column`、資料表、關鍵字與片段捷徑，並依 provider/database 快取資料表、View 與欄位 metadata。 |
| 程式碼片段 | ✅ | `Ctrl+Shift+P` 開啟片段管理器；支援 8 組內建片段、自訂片段 CRUD、全文搜尋、`$CURSOR$` 定位、保留縮排插入，以及 JSON 匯入／匯出工作區格式。 |
| 視覺化解釋 | ✅ | MySQL／MariaDB、PostgreSQL、SQL Server、Oracle、SQLite 都有原生唯讀計畫，可查看節點樹、屬性、原始資料、文字與可用成本。 |
| 視覺查詢建構器 | 📋 | 待補拖拉資料表、JOIN、條件與 SQL 雙向更新。 |
| Procedure／Function 偵錯器（中斷點、逐步、變數、呼叫堆疊） | 📋 | 依 provider 能力分階段實作，優先 PostgreSQL／SQL Server。 |
| AI 助理／詢問 AI | 🟡 | 核心聊天與 schema 上下文已完成，進階動作見上表。 |
| 資料傳輸／遷移（跨 DBMS） | 🟡 | 已有 Table／View 跨 provider 複製；待補整庫精靈、mapping、續傳與驗證報告。 |
| 資料同步 | 📋 | 先做唯讀資料差異、方向選擇與 SQL 預覽，再開放同步執行。 |
| 結構同步 | 🟡 | 兩庫唯讀結構差異報告已完成，可跨 provider 比對 Table、Column、PK 與 FK 並匯出 HTML；待補產生／審核 DDL。 |
| 模型 | 🟡 | 已有五種 provider 共用的唯讀 ER 圖與兩庫結構差異報告；拖曳編排、多模型與雙向同步仍在後續排程。 |
| BI | 📋 | 見 Navicat 17 BI 路線。 |
| 匯入／匯出（Excel、Access、CSV、ODBC 等） | 🟡 | MySQL SQL 匯入／匯出完整，查詢結果有常用格式；待補五種 provider 精靈對等化、Access／ODBC。 |
| 資料字典 | 🟡 | HTML 核心已完成，範本／直接 PDF／排程／郵件待補。 |
| 資料產生器（規則、約束、參照完整性、大量資料） | 🟡 | 已能依欄位型別產生 INSERT；待補規則編輯、FK 順序、唯一性與大量批次。 |
| 備份／還原與原生工具介面 | 🟡 | 已有邏輯 SQL 備份、隔離還原、差異與完整性排程；待補 MongoDump、Oracle Data Pump、SQL Server native backup 介面。 |
| 自動執行：查詢、匯入／匯出、傳輸、通知郵件 | 🟡 | 已有可攜式查詢／匯出／備份作業、立即執行、每日 Windows 工作排程與 JSON 紀錄；待補匯入、跨庫傳輸、郵件／Webhook、重試及更多觸發條件。 |
| MongoDB 結構描述分析器 | 🟡 | 第一期會抽樣前 100 筆文件推斷欄位型別、NULL 與 `_id`；待補巢狀路徑統計、異常與極端值檢視。 |
| Redis Pub/Sub | 📋 | 隨 Redis／Garnet provider 實作。 |
| 協同合作：同步連線、查詢、pipeline、片段、模型、BI、群組 | 📋 | 先做本機可匯出／匯入的工作區格式與 Git 版控，再補可自架同步服務與權限。 |
| SSH tunnel、SSL/TLS | ✅ | 四種網路 provider 已有共用安全設定 UI、憑證驗證、SSH SHA256 主機金鑰固定與隧道生命週期；SQLite 為本機檔案，不適用網路層設定。 |
| PAM／LDAP／Kerberos／MFA／SSO | 📋 | 依 provider 驗證能力分階段加入，不保存明文祕密。 |
| 深色模式／平台原生設計 | ✅ | Windows 原生 WinForms、淺／深色主題與 DPI 向量圖示已完成。 |
| 跨平台授權／Windows、macOS、Linux | 🟡 | 商業授權本身不適用開源專案；Avalonia 跨平台版已可使用 MySQL / MariaDB、PostgreSQL、SQL Server、SQLite 的連線、metadata、SQL 工作流程、CSV / TSV / JSON 結果匯出、Primary Key 穩定分頁，以及常用 scalar、1 MiB 內 binary／JSON／XML、PostgreSQL 網路位址與 SQL Server legacy LOB 欄位的安全 Table 資料編輯，並可選擇以 Linux Secret Service 或 macOS Keychain 保存密碼。Linux x64／ARM64 使用 self-contained tar 安裝包，macOS Intel／Apple Silicon 使用 `.app.zip`，並可依 RID 安全檢查、下載及驗證最新 Release；Linux 已完成交易式套用、啟動健康檢查與 rollback，待補 macOS Developer ID/notarization、自動套用、其餘 provider 與進階功能。 |

## Provider 與服務覆蓋

| 範圍 | 狀態 | 說明 |
| --- | --- | --- |
| MySQL／MariaDB | ✅ | 共用 MySQL provider，已有實機版本矩陣。 |
| PostgreSQL、SQL Server、Oracle、SQLite | 🟡 | 核心 metadata／查詢／編輯／DDL／備份可用，進階功能持續對等化。 |
| MongoDB | 🟡 | 第三期完成：連線、metadata、JSON find 查詢、文件樹、安全編輯與文件新增／刪除都已具備；standalone 4.4／7.0／8.0 實機矩陣通過。待補 Atlas／SRV 驗證環境矩陣與 Aggregation Pipeline。 |
| Redis／Garnet | 🟡 | 第三期完成：URI、ACL／密碼、TLS、logical db、key 瀏覽、受限查詢與五種型別的安全編輯／TTL／刪除；Redis 6.2、Redis 7 與 Garnet standalone 各 39 項實機矩陣通過。Cluster／Sentinel、監控與 Pub/Sub 待補。 |
| Snowflake | 🟡 | 第二期 provider 完成（SQL REST API、PAT／OAuth、metadata、分頁瀏覽、SELECT／SHOW 與查詢編輯器單一 DML／DDL）；真實帳戶實機矩陣、key-pair JWT、參數綁定、網格寫回與 bulk load 待補。 |
| AWS、Microsoft Azure、Google Cloud、Oracle Cloud、MongoDB Atlas、Redis Enterprise Cloud、Alibaba Cloud、Tencent Cloud、Huawei Cloud | 🟡 | RDBMS、MongoDB 與 Redis 可先用標準主機連線；待補各家 IAM／SSO／MFA 與雲端專用驗證。 |
| OceanBase、PingCAP／TiDB、Dameng、Fujitsu、Kingbase、HighGo | 📋 | 建立實機相容矩陣；能沿用 MySQL／PostgreSQL 協定者先驗證差異，其餘再建立專用 provider。 |

## 接續順序

1. ✅ 唯讀 ER 圖與兩庫結構差異報告第一版已完成。
2. ✅ Windows 自動執行＋查詢／匯出／備份作業與記錄第一版已完成。
3. ✅ SSH tunnel＋SSL/TLS 選項 UI、憑證驗證與排程共用連線流程已完成。
4. ✅ RDBMS、MongoDB 與 Redis 的連線 URI 匯入及設定頁確認流程已完成。
5. ✅ 連線星號、持久化色彩與批次屬性操作已完成。
6. 🟡 MongoDB 第一～三期、Redis／Garnet 第三期（string＋集合型別安全編輯，含實機矩陣）、Snowflake 第二期（SQL REST API 查詢＋查詢編輯器 DML／DDL）已完成。下一步候選：Snowflake 實機驗收、key-pair JWT、參數綁定與網格寫回，MongoDB Atlas／SRV 驗證矩陣與 Aggregation Pipeline，Redis Cluster／Sentinel／Pub/Sub，或回頭補模型／BI 路線。
7. 🟡 Linux / macOS 跨平台第二階段進行中：獨立 Core、Avalonia UI、四種 RDBMS workflow、系統密碼庫、結果安全匯出、Table optimistic concurrency 編輯、1 MiB 內 binary hex／JSON／XML、PostgreSQL 網路位址、SQL Server legacy LOB 編輯與 200 列穩定分頁、四架構 self-contained CI／Release 資產，以及依 RID 與 sidecar 完成串流 SHA-256 的安全更新下載已完成；Linux 安裝／啟動／解除安裝納入 Xvfb smoke，並已支援交易式套用、啟動健康檢查、失敗 rollback 與舊版重啟。下一步是 macOS Developer ID/notarization 與自動套用、ARM64 實機啟動，再逐步補其餘進階型別。
