# mySQLPunk 待辦事項與未來規劃 (TODO)

本文件整理自當前專案已知限制、跨平台移植進度與後續功能規劃。長期藍圖亦可參閱 [`docs/ROADMAP.md`](docs/ROADMAP.md)。

---

## 🚀 SQL 傾印 (備份) 與匯入功能對齊計畫（以 MSSQL 標竿為準）

前陣子 shadowjohn 為 SQL Server (MSSQL) 建立了極高標準的**「邏輯 SQL 傾印匯出精靈 (`MssqlExport`)」**與**「四步原生 SQL 匯入精靈 (`MssqlImport`)」**。本計畫旨在將其他主流資料庫（MySQL、SQLite、PostgreSQL、Oracle）全面對齊此一嚴謹規格。

### 📌 MSSQL 目前的標竿規格 (Gold Standard)

1. **SQL 傾印 (匯出) 精靈**：
   - **四步精靈引導**：選取資料表與相依檢視 (支援篩選與全選) ➔ 輸出路徑與一致性快照設定 ➔ 確認物件清單與預估 ➔ 執行進度、Punky 整理資料動畫、即時傳輸速率與完成回報。
   - **串流式檔案處理**：全串流寫入本機私有 Staging 檔案後原子替換，**絕不**將整庫拼接為記憶體字串，徹底杜絕 OutOfMemory (OOM)。
   - **超大欄位分塊串流**：LOB、XML、二進位與原生 Geometry 資料支援分塊 (Chunked) 串流寫入，單列可跨批次。
   - **交易一致性快照防護**：支援可選的一致性讀取快照 (如 MSSQL SNAPSHOT 隔離)，防範外鍵關聯不一致；若變更 DB 設定具備專用逾時預算與故障復原日誌 (Recovery Journal)。
   - **Footer Native Manifest**：匯出檔結尾附帶清單摘要（資料表數量、逐表行數 COUNT_BIG、SHA-256 核對碼），作為還原校驗依據。
   - **官方 CLI 相容性**：產出的 SQL 嚴格相容官方 sqlcmd 與 Go sqlcmd。

2. **SQL 檔匯入精靈**：
   - **四步精靈引導**：選擇檔案與目標資料庫 ➔ 完整串流預檢 (Preflight) ➔ 確認執行與錯誤政策 ➔ 執行即時進度、Punky 動畫與音效切換、錯誤紀錄。
   - **串流預檢 (Preflight)**：預先掃描編碼 (嚴格 UTF-8 或帶 BOM 的 UTF-16)、批次分隔符 (GO)、單批次上限 (32 MiB) 與目標資料庫連線/空庫狀態，不預先執行任何 SQL。
   - **專屬連線 Session**：全腳本依序在同一專用 Session 執行，保留暫存表 (#temp)、SET 旗標、交易與分段 LOB 狀態。
   - **彈性錯誤政策**：預設遇錯停止；在安全前提下支援「略過失敗批次並繼續」（限無未完交易與安全 LOB 狀態）。
   - **獨立日誌記錄**：每次執行於 `%LOCALAPPDATA%\mySQLPunk\ImportLogs` 建立獨立 Log 檔，保留所有錯誤代碼、行號與 Probe 清理紀錄，不覆蓋舊檔。
   - **Manifest 自動核對**：還原完成後，自動讀取結尾 Manifest 比對資料庫現存資料表數與列數。

---

### 🔍 各資料庫 Provider 現況缺口盤點

| 資料庫 Provider | SQL 傾印 (匯出) 現況 | SQL 檔案匯入現況 | 主要缺口與風險 |
| :--- | :--- | :--- | :--- |
| **MySQL / MariaDB** | 四步精靈、選表／模式、獨立連線、InnoDB 快照、generated 欄位投影與原子發布已補強。 | 共用預檢／執行 lexer、識別字限定的目標處理、衝突策略、交易／清理結果與錯誤日誌已補強。 | 本機回歸後仍需實機往返；來源單列目前限 1 MiB、INSERT 限 8 MiB。更大 LOB 分段與主動中斷執行中命令尚待擴充。 |
| **SQLite** | 原生逐列 reader、來源快照、generated 欄位投影、rowid／序號、文字／BLOB bytes 與原子發布已補強。 | 策略值綁定、DROP 保護、trigger 解析、嚴格 UTF-8、交易／PRAGMA 清理與日誌已補強。 | 私有 SQLite 往返驗收；單列目前限 8 MiB。SpatiaLite／GeoPackage 擴充模組與多 GiB、實體 UI 仍需另外驗收，不能只靠筆數宣稱無損。 |
| **PostgreSQL** | ✅ 已具備完整四步精靈 (`PostgreSqlExportWizardForm`)，支援選表/View、PostGIS 空間幾何無損 Hex EWKB 傾印、排除 spatial_ref_sys、REPEATABLE READ 快照與 Native Manifest。 | ✅ 已具備完整四步精靈 (`PostgreSqlImportWizardForm`)，支援狀態機 Lexer (精準解析 Dollar Quotes 與巢狀註解)、PostGIS 自動安裝開關、4種衝突策略、2,000行滑動視窗日誌與列數核對。 | 已達標（完全對齊 MSSQL 標竿規格，並深度整合崩琦 GIS PostGIS 空間特性）。 |
| **Oracle** | 獨立連線、來源 READ ONLY、原生逐列 reader、Spatial 原始 metadata／SRID、bounded LOB 與原子發布修整。 | 檢查 provider 結果、目標 owner、交易、共用 lexer、日誌與進度 bytes 修整。 | 仍待 Oracle／Spatial 實機往返；大型值、identity、LRS／曲線及跨 owner 腳本有明確支援邊界，詳見修整驗證文件。尚未宣稱完全對齊 MSSQL。 |

---

### 🎯 實施評估與建議順序

#### 🥇 第一優先：MySQL / MariaDB（推薦首選）
- **原因**：
  1. `mySQLPunk` 的核心品牌與最高頻使用情境正是 MySQL！
  2. 既有程式碼庫中已具備相對成熟的 `MySqlExportService` 與 `MySqlImportService` 底層邏輯（如 `DELIMITER` 解析、`SHOW CREATE` 提取、交易開關等），只需將底層串流強化並重構對齊 MSSQL 的四步精靈架構。
  3. 投資報酬率最高，能快速在主要客群中展現一致且專業的產品體驗。
- **具體工作**：
  - [x] 匯出：移植四步精靈（選表/View ➔ 輸出設定/single-transaction ➔ 確認物件 ➔ 串流執行 + Punky 動畫 + Manifest 生成）。
  - [x] 匯入：移植四步精靈（選檔 ➔ 預檢掃描 DELIMITER/編碼/單批上限 ➔ 確認目標與策略 ➔ 專用 Session 執行 + Punky 動畫 + 獨立 Log + Manifest 核對）。

#### 🥈 第二優先：SQLite（崩琦 GIS 招牌特色對齊）
- **原因**：
  1. 單機檔案型資料庫，無遠端網路干擾或伺服器權限設定，建置回歸與驗證最迅速。
  2. 徹底解決過去舊版 SQLite 匯出/匯入記憶體拼接造成大庫 OOM 的致命缺點。
  3. 深度支援 SpatiaLite 與 GeoPackage 空間幾何無損傾印 (`X'...'` HEX)、排除龐大空間中繼表 (`spatial_ref_sys`) 與虛擬表影子表 (`_node`, `_rowid` 等)。
- **具體工作**：
  - [x] 匯出：改用真正的串流 StreamWriter 寫檔；支援 `BEGIN IMMEDIATE` 一致性交易；支援側車註解 (Sidecar Comment) 隨結構導出；空間幾何 BLOB 無損 Hex 傾印；排除 R-Tree 影子表；產生 Native Manifest。
  - [x] 匯入：串流狀態機 Lexer 解析；支援 SpatiaLite 動態模組自動加載；交易包覆與 PRAGMA 性能優化；4種物件衝突策略；2,000 行滑動視窗終端機日誌；Manifest 逐表筆數核對。

#### 🥉 第三優先：PostgreSQL（崩琦 GIS PostGIS 空間特長對齊）
- **原因**：
  1. 企業與現代空間資訊系統（GIS）廣泛使用，為崩琦 GIS 核心亮點。
  2. 語法複雜度較高：需要支援 Dollar-quoted (`$$` 或 `$tag$`) 函數/預存程序本體、Bytea Hex 轉義、Array、Jsonb 與幾何 (EWKB) 原生字面量。
  3. 導入 PostgreSQL 原生的 `REPEATABLE READ` 交易快照保證匯出一致性，並無損傾印 PostGIS 幾何資料。
- **具體工作**：
  - [x] 匯出：支援四步精靈（選表/View ➔ 輸出設定/快照隔離/排除 spatial_ref_sys ➔ 物件清單 ➔ 串流執行 + Punky 動畫 + Manifest 生成）；PostGIS geometry/geography/raster 轉無損 EWKB 十六進位字面量；保留 GIST/SP-GIST 空間索引。
  - [x] 匯入：支援四步精靈（選檔 ➔ 串流狀態機預檢 Dollar Quotes 與巢狀註解 ➔ 4種物件衝突策略與 PostGIS 自動加載 ➔ 串流執行 + Punky 動畫 + 2,000行滑動視窗終端機日誌 + 獨立 Log + Manifest 逐表筆數核對）。

#### 🎖️ 第四優先：Oracle（崩琦 GIS Oracle Spatial 空間特長對齊）
- **原因**：
  1. 企業級大型資料庫與政府空間資訊系統（如國土測繪、地籍圖資、水利圖資）廣泛採用 Oracle Spatial (`MDSYS.SDO_GEOMETRY`)，為崩琦 GIS 核心場景。
  2. 需處理 PL/SQL 特殊分隔語意（內部多個邏輯分號 `;`，必須以獨立行 `/` 作為區塊終止符）。
- **具體工作**：
  - [x] 匯出：支援四步精靈（選表/View ➔ 輸出設定/唯讀交易快照/排除空間影子表 ➔ 物件清單 ➔ 串流執行 + Punky 動畫 + Manifest 生成）；探測 `SDO_GEOMETRY` 空間幾何欄位並轉為無損 `SDO_UTIL.FROM_WKBGEOMETRY(HEXTORAW('...'))`；產出 `USER_SDO_GEOM_METADATA` 空間中繼字典註冊；保留 `INDEXTYPE IS MDSYS.SPATIAL_INDEX` 空間索引；預設排除 `MDRT_*`、`MDXT_*`、`MDRS_*` 內部影子表。
  - [x] 匯入：支援四步精靈（選檔 ➔ 串流狀態機預檢 PL/SQL 獨立斜線 `/` 與空間幾何關鍵字 ➔ 4種物件衝突策略 ➔ 串流執行 + Punky 動畫 + 2,000行滑動視窗終端機日誌 + 獨立 Log + Manifest 逐表筆數核對）。

---

## 🐧 Linux / macOS 跨平台版功能遷移

跨平台預覽版目前以 Avalonia (.NET 8) 打造，核心資料庫功能已具備，以下功能正分階段由 Windows 完整版遷移：

- [ ] **更多 Provider 支援**
  - [ ] Oracle 連線與基本查詢
  - [ ] MongoDB 文件瀏覽與編輯
  - [ ] Redis / Microsoft Garnet 鍵值瀏覽與監控
  - [ ] Snowflake 直連與查詢
- [ ] **進階工作台工具**
  - [ ] 視覺化 ER Diagram（實體關聯圖）
  - [ ] 資料庫結構差異比較 (Schema Diff)
  - [ ] 資料表設計器 (Table Designer) 完整版
  - [ ] Punky AI 助理跨平台整合
  - [ ] 自動補欄位註解與字典功能
- [ ] **打包與簽章**
  - [ ] macOS 申請 Apple Developer ID 簽名與 Notarization (公證)
  - [ ] Linux 更多發行版套件包裝 (deb, rpm, Flatpak)

---

## 🗄️ 各資料庫 Provider 進階功能補齊

### MySQL / MariaDB
- [ ] 支援更多特殊 Collation 的無損樂觀並行比對
- [ ] 進階 Partition 管理與優化

### SQL Server
- [ ] 降版相容搬移：擴大支援非 dbo schema、包含部分索引與約束的搬移
- [ ] 跨版本實機測試涵蓋率擴充 (SQL Server 2016–2025)
- [ ] 物件名稱含 `.` 時的 schema.table 精確解析修復

### MongoDB
- [ ] 視覺化 Aggregation Pipeline 查詢建立器
- [ ] MongoDB Atlas / SRV 環境專用認證與實機驗收
- [ ] 支援 SSH Tunnel 轉發連線

### Redis / Microsoft Garnet
- [ ] 支援 Redis Cluster 與 Sentinel 拓撲架構
- [ ] 連線斷線自動重連機制與 Pub/Sub 歷史訊息保存
- [ ] 效能趨勢圖表與記憶體即時分析

### Snowflake
- [ ] Key-pair JWT 身份驗證支援
- [ ] 查詢參數綁定與網格雙向寫回
- [ ] 暫存區 (Stage) 管理與 Bulk Data Load

---

## 🛠️ 核心效能與體驗優化

- [ ] **記憶體串流最佳化**
  - [ ] XLSX 超大資料集匯出改為基於 OpenXML 串流寫入，避免高記憶體佔用
  - [ ] 超大 SQL 備份還原支援分段串流解析與執行
- [ ] **網路與代理**
  - [ ] 加入 SOCKS5 代理支援（讓資料庫連線可透過 SOCKS5 穿透）
- [ ] **程式碼簽名**
  - [ ] Windows 安裝檔加入 Code Signing 憑證，消除初次下載的 SmartScreen 警告
