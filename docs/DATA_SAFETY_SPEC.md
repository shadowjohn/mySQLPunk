# mySQLPunk 型別安全與並行衝突防護規範

mySQLPunk 的核心設計哲學是：**「絕不在使用者不知情的情況下遺失精度、靜默截斷資料或寫入過期變更」**。

為此，無論在 Windows 完整版還是 Linux / macOS 跨平台版本中，mySQLPunk 均實作了嚴格的型別校驗、Optimistic Concurrency Control（樂觀並行控制）與防護機制。

---

## 樂觀並行控制 (Optimistic Concurrency Control)

在編輯資料表內容時，mySQLPunk 遵循以下規則避免寫入衝突 (Stale Updates)：

1. **Primary Key 必要性**：修改與刪除強制要求具備 Primary Key。無主鍵資料表僅開放瀏覽首頁，禁止無確定定位的 UPDATE / DELETE。
2. **Byte-exact 原值比對**：
   - 字串在 UPDATE / DELETE 比對時採用原始 Byte 序比對，避免受到寬鬆 Collation（例如大小寫不敏感、重音忽略或尾端空白忽略）的影響而誤改其他列。
   - SQLite、PostgreSQL 與 MySQL/MariaDB 均會在單列交易中精確比對載入當下的原始值；若已被其他連線更改，會主動 Rollback 並提示衝突。
3. **複雜型別 Canonical 比對**：
   - PostgreSQL `json` 會以 UTF-8 原生文字比對保留格式；`jsonb` 採語意等號。
   - Array、Composite Type 與自訂型別統一比對 Canonical 文本序。

---

## 各引擎嚴格型別校驗

### 1. MySQL / MariaDB
- **Fail-Closed Diagnostics Warning**：
  在非嚴格模式 (non-strict mode) 下，MySQL 發生欄位截斷常只回報 Warning 而不報錯。mySQLPunk 在每一次資料寫入 Commit 前均會讀取連線 Diagnostics；只要偵測到 Warning，便立即 Rollback，防止資料在無聲中被截斷。
- **ENUM & SET 嚴格驗證**：
  從 Metadata 解析宣告清單，送出前以 Ordinal 與字串精確比對，防止被 Collation 靜默替換。
- **數值邊界**：
  依 `TINYINT` 至 `BIGINT` 及 `UNSIGNED` 邊界校驗，完整支援 `BIGINT UNSIGNED` 最高達 `18446744073709551615`。
- **空間資料**：
  Geometry / Spatial 支援 `SRID=<ID>;<WKT>` 格式，並在寫入時同步核對 SRID 與 WKT 拓撲。

### 2. PostgreSQL
- **高精度數值**：
  `NUMERIC` 支援任意宣告精度與負 scale，不進行無聲四捨五入。
- **日期與時間**：
  `date` 完整無損支援 4713 BC–5874897 AD 與 `±infinity`；`interval` 保留 `months`、`days`、`microseconds` 三維度分量。
- **網路與自訂型別**：
  `inet` / `cidr` 嚴格核對 prefix 與 host bits；完整支援 `tsvector`、`tsquery`、Range / Multirange、`hstore` 與 `ltree`。

### 3. SQL Server
- **Collation 與字元無損轉換**：
  `char` / `varchar` 依目標欄位 Collation 轉碼，無法由 Legacy Code Page 表示的字元直接回滾，絕不靜默變成 `?`。
- **高精度日期**：
  `datetime2` 支援 100ns 精度與從西元 0001 年起算，絕不降級為 1753 年的舊 `datetime`。
- **特殊型別**：
  支援 `hierarchyid` 路徑語法及 `sysname` / Alias Type 的 Base Type 驗證。

### 4. SQLite
- **DECIMAL 精度保護**：
  避免 Microsoft.Data.Sqlite 將 Decimal 悄悄轉成失去精度的 REAL，嚴格遵循 Canonical 數值表示。
- **嚴格 ISO 時間與 GUID**：
  以標準格式驗證並以 TEXT 參數原樣寫入，避免被 SQLite Affinity 模糊化。

---

## 安全匯出規範
- **試算表公式注入防護 (CSV Injection / Formula Injection)**：匯出 CSV/TSV 時，自動對以 `=`, `+`, `-`, `@`, `\t`, `\r` 開頭的字串進行轉義防護。
- **原子寫檔 (Atomic Write)**：所有匯出與設定存檔均先寫入私有暫存 Staging 檔案，校驗成功後再進行原位替換，避免中途斷電或崩潰產生壞檔。
