# Changelog

## [Unreleased]

### 🚀 新增功能

- **MySQL Table Designer 型別屬性面板**：欄位下方新增 Navicat 風格的型別屬性區，選到整數或數值欄位時可設定 `AUTO_INCREMENT`、`UNSIGNED`、`ZEROFILL`，選到 `datetime`／`timestamp` 可設定 `ON UPDATE CURRENT_TIMESTAMP`；勾選自動遞增會同步設定 Not Null 與 Primary Key，SQL 預覽也會產生對應 DDL。
- **Punky 代為操作（代理模式）**：於「選項 > AI」開啟後，Punky 輸入區會出現「請 Punky 代為操作」勾選；勾選送出的請求會讓 Punky 以多回合工具迴圈直接操作應用程式——查詢資料、執行變更、開查詢分頁、切換與開啟既有連線、重新整理物件樹、導覽物件。回覆若含多項建議，會在下方呈現可勾選清單，勾選後一鍵依序執行。所有工具走統一文字協定（相容 API 與訂閱 CLI 後端），唯讀操作靜默執行，一般變更直接執行，破壞性操作（DROP、TRUNCATE、DELETE、無 WHERE 的 UPDATE 等）一律先跳出確認並顯示確切 SQL；每個操作都寫入查詢歷史稽核。只操作既有連線，永不要求或猜測帳號密碼；查詢結果在提示中明示為資料而非指令。整體為 opt-in，預設關閉。
- **跨模型答案並排比較**：Punky 輸入區新增「比較」，可替左右兩側各選一個服務與模型，把相同的問題、目前聊天室最近上下文及勾選的 schema 快照同時送出，並在獨立寬視窗左右呈現答案。兩次呼叫各自計量，任一側失敗只影響該欄；結果不加入聊天室、不自動執行或套用 SQL，只記住右側 provider 與模型名稱供下次選用。
- **Punky 多聊天室**：AI 面板可新增、切換、重新命名與關閉多段彼此隔離的對話；每段對話會分開保留本次執行期間的訊息上下文、未送出草稿、schema 勾選與 SQL 差異套用回呼。對話不寫入磁碟，最後一段不允許關閉，有內容的對話關閉前會再次確認；模型回覆期間會暫停切換，避免回覆串到別的聊天室。

### 🛠️ 問題修正與優化

- Google 個人帳號的 Gemini CLI 已停止服務，AI 助理現以官方 Antigravity CLI（`agy`）取代；既有 `gemini-cli` 設定會自動遷移，不沿用舊的 executable 或 Gemini 2.x model 覆寫。Antigravity prompt 會以官方 `stream-json` stdin 協定傳遞並只讀取 terminal `result`，不把 SQL/schema 放進命令列，也不使用危險的自動授權旗標。未偵測到 `agy` 時，卡片提供「官方安裝教學」與需二次確認的「自動安裝」；自動安裝只開啟可見的非提權 PowerShell 執行官方 HTTPS installer，不會略過本機 PowerShell 執行原則。安裝後會直接檢查官方使用者安裝目錄，不會因 mySQLPunk 尚未重啟、PATH 尚未刷新而誤判未安裝。
- AI 助理在偵測到 OpenAI Codex CLI 的 `models_cache.json` 與 CLI 版本不相容時，現在會提示使用 Codex Desktop 內建 CLI 或更新／移除舊版 npm `@openai/codex`，並明確標示該檔案不是 token 檔，不再把原始 CLI banner 直接丟給使用者。
- AI 助理預設解析 `codex` 指令時，若同時找到 npm shim 與 Codex Desktop 管理的 `codex.exe`，會優先使用 Desktop 版本，避免舊版 npm CLI 讀到新版 Desktop cache 後失敗；使用者在端點欄明確填入完整路徑時仍會尊重指定路徑。
- AI 訂閱 CLI 卡片的帳號偵測改為只確認登入資料檔是否存在，不再讀取 Codex／Claude 的 token-bearing 帳號檔案內容；Antigravity 使用 Windows Credential Manager 時直接標示為不支援帳號偵測，卡片會顯示登入資料狀態而不顯示帳號 email。
- Punky 右上齒輪開啟的選項視窗，按「確定」後現在會立即套用佈景主題與語言，行為與主選單「選項」一致。
- 主視窗啟動時預設置中螢幕顯示，只影響第一次開啟位置，不會鎖定視窗位置或阻止使用者拖曳調整。
- Database 的資料表/物件清單現在只允許選取、不會因誤點進入儲存格編輯；清單綁定完成後也會清除殘留的等待游標，避免滑鼠停在清單上仍顯示沙漏。資料表清單的「修改日期」固定顯示為 `yyyy-MM-dd HH:mm:ss`，不再受 Windows 區域格式影響出現 `下午` 等文化化時間字串。
- Table Designer 的欄位 grid 不再顯示 WinForms 內建的空白新增 placeholder 列，避免剛開啟設計資料表時誤以為 schema 多了一個空欄位；新增欄位仍透過工具列的「加入欄位／插入欄位」操作。
- MySQL Table Designer 既有資料表的「註解」分頁現在會載入原始 table comment，使用者修改後切到 SQL 預覽或離開輸入框時會產生 `ALTER TABLE ... COMMENT = ...`，不再只顯示「沒有偵測到變更」。
- 修正部分機器上 AI 訂閱 CLI「卡片偵測得到、按測試卻失敗」：npm 版 CLI 的 `.cmd` 啟動器要靠 PATH 找 node，但 GUI 程式繼承的 PATH 可能沒有（nvm/fnm 只寫在 shell 設定檔，或裝完 Node.js 還沒重開程式）。現在啟動前會從常見安裝位置與登錄檔 PATH 自動補上 Node.js；若 node 真的不存在，錯誤訊息也會明確指向缺 Node.js，而不是誤報「找不到該 CLI」。
- Linux／macOS 的 Avalonia DataGrid 更新至 12.1.2，納入垂直捲軸範圍同步、版面取整邊界捲動鏈結與 clipboard binding 型別繼承修正。
- Linux／macOS 查詢結果在欄位排序後匯出 CSV、TSV 或 JSON 時，現在會沿用目前網格的完整可視順序，不再悄悄回到原始查詢列序；匯出前也會驗證列索引是完整且不重複的排列。
- Linux／macOS 查詢結果欄位改以原始 provider 值做型別感知排序；NULL、跨 CLR 數字、日期、文字與 binary 都能安全比較，相同值保留資料庫回傳順序。畫面替代文字 `(NULL)` 不再與 nullable 數字／日期進行異質型別比較，也不會和資料本身真的等於 `(NULL)` 的字串混淆。

## [1.0.0.20] - 2026-08-30

### 🚀 新增功能

- **跨平台查詢結果安全複製**：Linux / macOS 結果網格支援多列選取，可用按鈕或 `Ctrl/Cmd+C` 把目前可視順序的完整欄位複製為含欄名 TSV；NULL 保持空欄、空字串保留引號，日期、數字與 binary 沿用匯出格式，`= + - @` 等試算表公式注入會先中和。內建 DataGrid 的不受限複製已停用，自訂流程限制 4 MiB 並只寫系統剪貼簿、不建立磁碟暫存檔。
- **跨平台本次查詢記錄**：Linux / macOS SQL 編輯器會保留本次程式執行期間成功送出的最近 50 筆 SQL，標示時間、provider、database 與選取範圍；同來源相同 SQL 會更新到最上方，合計限制 2 MiB。選取記錄只載回編輯器、不會自動執行，也可立即清除；為避免含敏感 literal 的 SQL 落盤，結束程式即清空且不寫設定檔。
- **跨平台資料庫物件搜尋**：Linux / macOS 主視窗可依 schema 或物件名稱即時搜尋，支援以空白分隔的多個條件與不分大小寫比對；也能只顯示 Table 或 View，並同步呈現篩選後與總物件數。搜尋只在已載入 metadata 上執行，不會額外掃描或查詢資料庫。
- **AI 格式化與資料庫語法轉換**：「詢問 AI」選單新增只調整排版的 SQL 格式化，以及 MySQL／MariaDB、PostgreSQL、SQL Server、Oracle、SQLite 五種目標方言；目前使用中的 provider 會停用。動作只建立 Punky 草稿，不會自動送出；回覆 SQL 仍需經過逐行差異預覽與變更區段勾選才能套回編輯器。
- **自訂與釘選 AI 動作**：查詢工具列的「詢問 AI」可建立自己的提示動作，決定是否直接釘選到選單；管理視窗支援新增、修改、刪除與立即使用。執行時只把選取範圍或目前 SQL 帶入 Punky 草稿，不會自動送出或執行；回覆若含 SQL，仍需經過差異預覽才能套回編輯器。自訂檔只保存名稱、提示與釘選狀態，不包含 SQL、連線資訊或 AI 認證。
- **查詢編輯器詢問 AI 與錯誤交接**：查詢工具列新增「詢問 AI」，可把選取範圍或目前 SQL 帶入解釋、最佳化草稿；查詢執行失敗後會啟用「修正上次執行錯誤」，一併附上資料庫類型、資料庫名稱與錯誤原因。草稿會先顯示在 Punky 面板供確認，不會自動送出；錯誤裡常見的 password、token、API key、secret 與 Bearer credential 會先遮蔽，也不會帶入主機、帳號或連線字串。最佳化與修正回覆可逐行並排比較，並勾選要採用的連續變更區段，確認後才套回當次選取範圍或全文；若編輯器已變更就拒絕覆寫，套用後也不會自動執行。
- **AI 訂閱 CLI 卡片與帳號偵測**：重做「選項 > AI」，用卡片列出 OpenAI Codex、Claude Code 與 Antigravity CLI 的安裝狀態、實際執行路徑與登入資料狀態，可直接切換目前使用的 CLI；Codex 會優先選用 Codex Desktop 內建 CLI，以避開舊版 npm shim 和新版 Desktop cache 的格式落差。API、Ollama、LM Studio 與 OpenRouter 設定保留在同頁下方，並依目前服務隱藏不適用的欄位。帳號偵測只確認可安全判定的 CLI 狀態，不讀取 token 或金鑰檔案內容，也不把找到登入資料誤當成已驗證訂閱權限。
- **Linux / macOS 跨平台預覽版與安裝資產**：保留既有 Windows WinForms 完整版，新增獨立 .NET 8 Core 與 Avalonia 桌面程式，可在 Linux / macOS 管理 MySQL / MariaDB、PostgreSQL 與 SQLite 連線，瀏覽 database、Table / View，執行 DDL / DML / SELECT、匯出 CSV／TSV／JSON 結果，並以主鍵與樂觀並行保護安全新增、修改或刪除 Table 資料。Release 會同時產生免另裝 .NET 的 Linux x64／ARM64 使用者層級安裝包與 macOS Intel／Apple Silicon `.app.zip`，每個資產附 SHA-256；CI 會在 Linux x64／ARM64 與 Intel／Apple Silicon macOS 原生 runner 分別驗證同架構安裝包、安全套用、啟動健康檢查與 rollback。macOS 預覽目前採 ad-hoc 簽署，Developer ID 與 Apple notarization 仍需發版環境提供憑證。
- **跨平台 SQL 文件工作流程**：Linux / macOS 可從按鈕、`Ctrl/Cmd+O` 或 `.sql` 檔案關聯開啟 SQL，並用 `Ctrl/Cmd+S` 儲存、`Ctrl/Cmd+Shift+S` 另存；支援嚴格 UTF-8 與帶 BOM 的 UTF-16，保留原始編碼並限制為 4 MiB。儲存採同目錄私有 staging 與原子替換，覆寫會比對載入時 SHA-256，外部修改或刪除時拒絕覆蓋；切換文件、關閉程式或套用更新前也會保護未儲存內容。Linux desktop entry 與 macOS app bundle 都註冊 `.sql` 文件。
- **Linux / macOS 安全更新檢查**：跨平台主視窗可手動檢查最新公開 GitHub Release，依目前 OS 與 CPU 精確尋找 `linux-x64`／`linux-arm64`／`osx-x64`／`osx-arm64` 安裝包及同名 `.sha256`；解析會拒絕非 GitHub、非 HTTPS、錯誤版本與不支援架構。Linux 與 macOS 都可在驗證後直接關閉、交易式套用並重新啟動；新版若無法通過啟動健康檢查會回復舊版並重新啟動。Developer ID/notarization 仍需發版環境提供 Apple 憑證。
- **跨平台更新包安全下載與 Linux rollback**：找到目前 RID 的安裝包與同名 `.sha256` 後，可直接選擇本機位置下載；程式會限制 sidecar 與安裝包大小、嚴格核對 sidecar 檔名、以串流計算 SHA-256，並先寫同目錄暫存檔，完整驗證成功才原子替換目標。Linux UI 會先原子取得跨視窗獨佔 lock；套用程序再複製並重驗雜湊、限制解壓路徑／類型／檔案數與展開大小，等待目前程序退出後才執行交易式 installer；新版啟動健康檢查失敗時會回復 launcher、desktop entry 與目標版本，重新啟動舊版並在 UI 顯示記錄位置。
- **跨平台 Table 大型資料分頁**：Linux / macOS Table 編輯器改為每頁 200 列，提供上一頁、下一頁、頁碼與實際列範圍；MySQL／MariaDB、PostgreSQL、SQL Server、SQLite 都會依 Primary Key 穩定排序，跨頁後仍沿用原始值樂觀並行保護。沒有 Primary Key 的 Table 為避免不穩定定位，只顯示第一頁並維持修改／刪除停用。
- **跨平台 Table 穩定欄位排序**：Linux / macOS Table 編輯器可選擇 scalar 或 Primary Key 欄位並切換遞增／遞減，由 MySQL／MariaDB、PostgreSQL、SQL Server、SQLite 在資料庫端排序；相同值一律追加 Primary Key 遞增 tie-breaker，跨頁不會漏列或重複。欄名會重新對照 metadata，JSON、XML、binary、spatial 與 SQL Server legacy LOB 等不適合跨 provider 排序的型別不會出現在選單，Core 也會拒絕繞過 UI 的不安全請求。
- **跨平台 Table 本頁安全匯出**：Linux / macOS Table 編輯器可把目前排序後載入的頁面直接匯出成 CSV、TSV 或 JSON；沿用查詢結果的 UTF-8、試算表公式注入防護、暫存檔與驗證後原子替換。檔案選擇與提示都明確標示只包含目前最多 200 列，位於後續頁或仍有下一頁時不會誤稱為完整 Table。
- **跨平台 Table 參數化篩選**：Linux / macOS Table 編輯器可對 metadata 白名單 scalar 欄位套用「精確等於／是 NULL／不是 NULL」，並與欄位排序及 PK 穩定分頁共同運作。欄名必須精確命中 metadata，值會先依欄位型別驗證再使用 provider 原生參數，未知欄名、偽造 SQL 片段，以及 binary／JSON／XML／spatial 等跨 provider 等號語意不一致的型別都會 fail closed。
- **跨平台 Table 欄位顯示控制**：Linux / macOS Table 編輯器可暫時隱藏不需要的欄位，網格與 CSV／TSV／JSON 本頁匯出會一致只保留可見欄位；完整原始資料列仍留在記憶體供修改與 optimistic concurrency 比對，至少保留一欄，未知匯出欄名也會 fail closed。
- **跨平台 Table 欄位偏好安全保存**：Linux / macOS 會依連線 ID、database、schema 與 Table 保存隱藏欄名，重開編輯器或程式後仍保留；偏好檔不記錄篩選值或資料列內容，採 1 MiB／500 Table 上限、原子替換與 Unix `0600`。刪除連線會同步清除該連線偏好，schema 變更若使全部現存欄位被舊設定隱藏，會安全退回全部顯示。
- **跨平台 binary 安全編輯**：Linux / macOS Table 編輯器可用明確 `0x` 十六進位格式新增或修改 MySQL／MariaDB BLOB、PostgreSQL bytea、SQL Server binary／varbinary 與 SQLite BLOB；輸入必須是偶數個 hex 字元且上限 1 MiB，仍使用 binary 參數與原始值樂觀衝突防護。超過 1 MiB 的既有值只顯示前 16 bytes 與總長度並維持唯讀，避免編輯器建立超大型 hex 字串或 optimistic WHERE 參數。
- **跨平台 JSON 安全編輯**：Linux / macOS Table 編輯器可新增或修改 MySQL／MariaDB JSON、PostgreSQL json／jsonb 與 SQLite JSON 欄位；儲存前以嚴格 JSON parser 拒絕空值、註解、尾端逗號、超過 64 層或 1 MiB 的輸入，並依 provider 指定 JSON 參數與語意正確的原始值比對。超過 1 MiB 的既有 JSON 只顯示摘要並維持唯讀，避免大型文字進入編輯器或 optimistic WHERE 參數。
- **跨平台 XML 安全編輯**：Linux / macOS Table 編輯器可新增或修改 PostgreSQL xml、SQL Server xml 與 SQLite XML 欄位；儲存前拒絕空值、畸形文件、多根節點、DTD／外部實體、超過 64 層或 1 MiB 的內容，並依 PostgreSQL／SQL Server 的原生 XML 型別指定參數及安全原值 predicate。超過 1 MiB 的既有 XML 只顯示摘要並維持唯讀。
- **跨平台 PostgreSQL 網路位址編輯**：Linux / macOS Table 編輯器可安全新增或修改 PostgreSQL inet、cidr、macaddr 與 macaddr8；輸入會嚴格驗證 IPv4／IPv6 prefix、CIDR host bits、IPv6 zone identifier 與 6／8-byte MAC 長度，再轉為 Npgsql 原生參數。Table 載入會由 PostgreSQL 轉成保留 subnet prefix 的文字，避免 driver 預設 `IPAddress` mapping 遺失 `/24` 等資訊。
- **跨平台 SQL Server legacy LOB 編輯**：Linux / macOS Table 編輯器不再把 SQL Server text、ntext 與 image 固定為唯讀；寫入會使用對應的 SqlClient 原生參數，text／ntext 的 optimistic-lock 會先轉成明確字元型別再做 binary 比對，image 則轉成 varbinary(max)，避開 deprecated LOB 不支援一般等號比較的限制。image 仍沿用 1 MiB binary hex 安全上限。
- **跨平台 MySQL／MariaDB BIT 編輯**：Linux / macOS Table 編輯器可用非負十進位整數安全編輯 BIT(1–64)；儲存前依欄位 bit width 計算上限並拒絕負數或溢位，再以 MySqlConnector 原生 Bit 參數寫入。MySQL 8 與 MariaDB 11.4 實機矩陣均覆蓋 BIT(8) 與 BIT(64) UInt64 最大值的 CRUD／衝突情境。
- **跨平台 PostgreSQL bit string 編輯**：Linux / macOS Table 編輯器可用純 0／1 字串安全編輯 PostgreSQL bit(n) 與 bit varying(n)；metadata 會保留宣告長度，固定長度欄位要求精確 bit 數、varying 欄位拒絕超過上限，並以 Npgsql BitArray 原生參數寫入。Table 載入會明確轉成文字，前導零不會因 CLR 型別 mapping 遺失。
- **跨平台 PostgreSQL 帶時區時間編輯**：Linux / macOS Table 編輯器可用 `HH:mm:ss.ffffff±HH:mm` 安全編輯 PostgreSQL time with time zone；輸入必須包含 offset，可保留最高微秒精度，並以 Npgsql TimeTz 原生參數寫入。Table 載入與 optimistic-lock 原值會在保留純時間顯示的同時嚴格轉回 DateTimeOffset，不混入無關日期。
- **跨平台 PostgreSQL interval 無損編輯**：Linux / macOS Table 編輯器可用 `months=<整數>;days=<整數>;microseconds=<整數>` 明確編輯 PostgreSQL interval 的三個原生分量，避免用 `TimeSpan` 把月數近似成固定天數；寫入使用 NpgsqlInterval，載入與 optimistic-lock 也逐一比較 months、days、microseconds，`1 month` 不會被誤當成 `30 days`。
- **跨平台 PostgreSQL WAL LSN 編輯**：Linux / macOS Table 編輯器可用 `XXXXXXXX/XXXXXXXX` 安全編輯 PostgreSQL pg_lsn；斜線兩側各限制為 1–8 個十六進位字元，輸入會正規化為大寫並以 NpgsqlLogSequenceNumber／PgLsn 原生參數寫入，完整涵蓋 64-bit 最大值與 optimistic-lock 衝突防護。
- **跨平台 PostgreSQL 系統識別碼編輯**：Linux / macOS Table 編輯器可安全編輯 oid、xid、cid 與 xid8；前三者保留完整 UInt32 範圍，xid8 保留完整 UInt64 範圍，並分別使用 Npgsql Oid／Xid／Cid／Xid8 原生參數，避免有號整數轉換造成溢位或截斷。
- **跨平台 PostgreSQL 全文檢索型別編輯**：Linux / macOS Table 編輯器可安全編輯 tsvector 與 tsquery，保留 lexeme、位置、權重、prefix、NOT 與 phrase-distance operator；因 Npgsql 8 已將 client parser 標示為不可靠且 obsolete，值會以大小受限的 Unknown 參數交由 PostgreSQL 在目標欄位上下文權威解析，仍不把輸入拼入 SQL。畸形 tsquery 會由 server 拒絕並回復整筆交易。
- **跨平台 MySQL／MariaDB ENUM／SET 編輯**：Table 編輯器會辨識完整 COLUMN_TYPE 宣告，以 MySqlConnector Enum／Set 原生參數安全新增與修改；SET 由 server 依宣告順序正規化，strict mode 遇到未宣告 ENUM 值會拒絕並回復交易。MySQL 8 與 MariaDB 11.4 實機矩陣及 MariaDB Linux UI 均已驗證。
- **跨平台 MySQL／MariaDB TIME 完整範圍編輯**：不再用只適合一般時間的 `.NET TimeSpan.Parse` 讀取 MySQL TIME；專用 parser 支援負值、超過 24 小時、宣告的 0–6 位小數秒與完整 ±838:59:59 範圍，並在絕對邊界拒絕額外微秒。Table 載入會保留 `HHH:mm:ss.ffffff` 原生格式，以 MySqlDbType.Time 寫入。MySQL 8、MariaDB 11.4 矩陣與 MariaDB Linux UI 均已驗證。
- **Snowflake 查詢編輯器寫入第二期**：保留 SELECT／SHOW 等結果集路徑，新增單一 INSERT／UPDATE／DELETE／MERGE 與 DDL 的 SQL REST API 執行，並從 DML ResultSet 回報 affected rows；帶參數呼叫會 fail closed，避免參數綁定尚未完成時靜默忽略值。loopback HTTP 測試加入實際 UPDATE request、回覆與 MERGE 多計數欄位驗證；資料網格寫回、bulk load 與真實帳戶矩陣仍留待後續。
- **Redis／Garnet 集合型別安全編輯**：key 編輯器依型別切換，hash 欄位新增／更新／刪除、list 既有元素編輯與尾端新增、set 成員新增／移除、zset 分數新增／更新／移除；所有寫入共用 WATCH＋MULTI/EXEC 交易，項目被其他連線改過、型別被重建或 EXEC 落空都回報衝突且不覆蓋，zset 分數以數值比較。Redis 6.2、Redis 7 與 Garnet standalone 實機矩陣擴充為各 39 項檢查；list 元素刪除因 Redis 無依索引刪除命令留待後續。
- **Snowflake 唯讀 provider 第一期**：新增 Snowflake 連線類型，以 SQL REST API v2 直連（Programmatic Access Token 或 OAuth token，token 存 Windows Credential Manager），不引入官方驅動的大型相依樹。支援 SHOW DATABASES 與 INFORMATION_SCHEMA metadata、schema.table／view 瀏覽、欄位與列數、GET_DDL、分頁資料檢視與唯讀 SELECT／SHOW 查詢；`snowflake://` URI 可匯入，未知參數一律拒絕。REST client 內建 202 輪詢與多 partition 合併；寫入於上方第二期接續完成，key-pair JWT 與真實帳戶實機驗收仍待後續。
- **Redis／Garnet string 安全編輯與實機矩陣**：查詢結果雙擊 key 或右鍵可開啟 key 編輯器，支援 string 值編輯、TTL 設定／移除與確認後刪除 key；儲存以 WATCH＋MULTI／EXEC 樂觀並行保護，被其他連線改過會回報衝突且不覆蓋，保留 TTL 時在同一交易內補 PEXPIRE，非 UTF-8 值標示唯讀避免損毀。Redis 6.2、Redis 7 與 Microsoft Garnet standalone 各通過 26 項實機檢查，可用 `tests/Run-RedisLiveMatrixTests.ps1` 重跑；集合型別寫入、Cluster／Sentinel 留待後續。
- **Redis／Microsoft Garnet 唯讀 provider 第一期**：新增 `redis://`／`rediss://` URI 匯入、ACL／密碼驗證、TLS 直連與 logical db 瀏覽；可用 SCAN 檢視 key 型別、TTL、文字摘要，並以 pattern、type 或單一 key 執行受限唯讀查詢。RESP 解析加入大小／巢狀防護，SCAN 單次 traversal 會去重且先套用型別篩選；目前完成 loopback RESP 整合測試，外部 Redis／Garnet 實機矩陣、Cluster／Sentinel、寫入、監控與 Pub/Sub 留待後續。
- **MongoDB 文件新增／刪除與實機矩陣**：結果網格右鍵可新增文件（空白文件插入模式、未給 `_id` 時自動產生、成功後轉一般編輯）與刪除文件（先依 `_id` 重讀完整文件、確認後以重讀比對＋完整文件過濾安全刪除）；view 一律擋下。standalone MongoDB 4.4／7.0／8.0 各通過 24 項實機檢查，涵蓋 metadata、查詢、型別保真、編輯／插入／刪除與各種並行衝突。
- **MongoDB 文件樹與安全編輯**：查詢結果列可雙擊或右鍵「開啟文件」開啟文件檢視器，左側為可展開的欄位／陣列／型別樹，右側以 Canonical Extended JSON 編輯（欄位型別不會被改變）；儲存前會鎖定 `_id`，寫回時以編輯前的完整文件做樂觀並行比對，文件被他人修改或刪除時回報衝突不寫入。View 與缺 `_id` 的文件維持唯讀，開啟時會依 `_id` 重新讀取完整文件，查詢 projection 不會造成欄位遺失。
- **MongoDB 唯讀 provider 第一期**：新增一般／SRV 連線與 URI 匯入，可瀏覽 database、collection、view、索引與統計資訊，抽樣推斷 schema，並以唯讀網格或受限 JSON find 查詢檢視文件；寫入及專用文件編輯器仍留在後續階段。
- **連線星號與批次屬性**：星號與色彩會隨目前連線設定檔保存，加星連線在樹中顯示 `★` 並置頂；工具選單與連線右鍵可勾選多筆連線，批次加／移星號、移動群組或套用色彩。
- **連線 URI 匯入**：新增連線精靈可匯入 MySQL／MariaDB、PostgreSQL、SQL Server、Oracle、SQLite、MongoDB 與 Redis URI，支援 percent encoding 與各 provider 對應的安全參數，解析後會先開啟原本的設定頁供確認；原始 URI 不會保存，密碼仍交由 Windows Credential Manager 儲存。
- **SSL/TLS 與 SSH 安全連線**：MySQL／MariaDB、PostgreSQL、SQL Server 與 Oracle 連線可設定憑證驗證和 SSH Tunnel；Tunnel 會固定 SHA256 主機金鑰指紋，測試連線、主畫面與自動執行作業共用同一套安全設定。
- **Windows 自動執行作業**：可從工具選單建立唯讀查詢、查詢結果匯出與 SQL 備份作業，支援立即執行、每日工作排程與 JSON 執行紀錄；作業檔不保存帳密，排程沿用同一個 Windows 使用者的 Credential Manager。

### 🛠️ 問題修正與優化

- 自動發版不再因少量修改累積到低分數門檻或經過七天就建立新版；只有明確標記重大里程碑、BREAKING CHANGE 或人工核准才會發版。重大批次會自動把完整 `[Unreleased]` 轉為下一版本段落，修正先前明明達門檻卻因缺少預建版本段落而只顯示 Gate 成功、不產生 GitHub Release 的問題；Release 驗證說明也同步反映 Linux x64／ARM64 都會在原生 runner 完成安裝、Xvfb 啟動、安全更新、rollback 與解除安裝。
- Linux / macOS SQL 編輯器現在會優先執行非空白的選取範圍，否則只執行游標所在 statement；provider lexer 會依 MySQL、PostgreSQL、SQL Server 與 SQLite 規則略過字串、quoted identifier、註解、dollar quote 與 PostgreSQL `E''` 內分號。MySQL 反斜線字串若受 `NO_BACKSLASH_ESCAPES` 影響而有兩種可能邊界會 fail closed、要求明確反白；整份文件改由獨立按鈕或 `Ctrl/Cmd+Shift+Enter` 執行，避免開啟多段 SQL 後誤送出未選取的 DDL／DML，本次查詢記錄也會區分三種來源。
- Linux／macOS Table 編輯器會從 MySQL／MariaDB 與 SQL Server metadata 保留 `BINARY(n)` 的固定 byte 數，並要求 hex 輸入剛好為 n bytes；較短值不再被 server 無 warning 補 `0x00`，較長值也不會被截斷。SQL Server alias type 沿用 base binary 長度，VARBINARY／BLOB／bytea／SQLite BLOB 仍維持可變長。
- Linux／macOS 的 MySQL／MariaDB Table mutation 現在會在 commit 前讀取同連線 `SHOW WARNINGS`；non-strict 模式若把超長 VARCHAR／VARBINARY／TINYTEXT／TINYBLOB 或不可表示字元降成 warning，整筆單列交易會立即 rollback 並回報 server code，不再顯示成功卻留下截斷／替換值。MySQL optimistic predicate 也改用 `CAST(... AS BINARY)`，移除新版 server 對 deprecated `BINARY expr` 的警告。
- Linux／macOS Table 編輯器的 PostgreSQL `date` 改用獨立 canonical 文字路徑，完整保留 4713 BC–5874897 AD 與 `±infinity`，也避免 Npgsql 把 `0001-01-01`／.NET 最小值誤送成 `-infinity`；一般 PostgreSQL／SQLite 日期只接受純日期，不再讓 timestamp 的時間部分被無聲丟掉。
- Linux／macOS Table 編輯器現在會依 PostgreSQL `interval(p)` 與 `YEAR`、`YEAR TO MONTH`、`DAY TO HOUR` 等欄位限制驗證三個原生分量；會被 server 無聲丟棄的較小欄位或超出宣告精度的小數秒會在送出前拒絕，不再取整後誤顯示為儲存成功。
- Linux 使用者層級 installer 會先建立 app、launcher 與 desktop entry 的完整 staging，三者都成功後才提交；任一切換失敗會原樣回復前一份 app 目錄與兩個入口，並清除中間檔，避免更新途中留下無法啟動的半安裝狀態。
- PostgreSQL 連線字串改由 provider builder 組裝，排程連線遇到分號或等號等特殊密碼字元時不會被錯誤切割。

## [1.0.0.19] - 2026-08-26

### 🚀 新增功能

- **五種資料庫執行計畫**：SQL Server、Oracle、SQLite 加入原生唯讀執行計畫，現在可和 MySQL／MariaDB、PostgreSQL 一樣查看節點樹、原始資料、文字計畫、預估列數與可用成本。
- **唯讀 ER 圖**：五種 provider 共用 schema 快照，能在可停靠分頁顯示資料表欄位、主鍵與外鍵關聯，並提供縮放、適合視窗、中鍵平移、重新整理與 PNG 匯出。
- **資料庫結構差異報告**：可選擇兩個已開啟的資料庫，比對資料表、欄位型別、空值、主鍵與外鍵，並在可停靠分頁交換方向、重新比較及匯出 HTML；流程全程唯讀。

### 🛠️ 問題修正與優化

- SQL Server 會在同一連線開啟 `SHOWPLAN_ALL` 並於成功或失敗後關閉；Oracle 的 `PLAN_TABLE` 資料使用獨立 statement ID 並在讀取後清理；SQLite 使用 `EXPLAIN QUERY PLAN`，三者都不會執行原本的 DML。
- 外鍵 metadata 依 MySQL `KEY_COLUMN_USAGE`、PostgreSQL `pg_constraint`、SQL Server `sys.foreign_keys`、Oracle `ALL_CONSTRAINTS` 與 SQLite `foreign_key_list` 正規化；個別 metadata 失敗時保留其餘圖面並回報警告數。
- 結構差異會正規化常見跨 provider 型別別名，外鍵改以欄位與參照目標比對，不會因 constraint 名稱不同誤報；連線放在群組下時也能正確辨識資料庫節點。

## [1.0.0.18] - 2026-08-26

### 🚀 新增功能

- **右側窗格分隔線**：物件詳細資料與 AI 助理同時展開時，兩個窗格之間會顯示符合目前明暗主題的細分隔線。
- **SQL 程式碼片段**：查詢視窗新增 `Ctrl+Shift+P` 片段管理器，提供 8 組內建範本與自訂片段搜尋、新增、刪除、插入、JSON 匯入／匯出；`$CURSOR$` 可指定插入後游標位置。
- **上下文自動完成**：自動完成可解析目前 statement 的資料表、View、JOIN 與 alias，提供欄位及 `alias.column` 建議，並將 provider/database metadata 寫入既有自動完成快取。
- **具名資料表設定檔**：資料表資料模式可保存多組篩選條件、排序欄位／方向與顯示欄位，並從底部工具列快速切換；五種既有 provider 會套用各自的安全分頁語法。
- **物件 URI 分享與定位**：database 與支援的資料庫物件可從左側樹或清單右鍵複製 `mysqlpunk://object` URI；由 URI 啟動時會沿用目前連線設定檔開啟連線、載入 metadata 並定位物件。

### 🛠️ 問題修正與優化

- 任一窗格關閉或暫時收合後會同步隱藏分隔線，不會在主內容與右側收合列之間留下多餘線條。
- SQL 自動完成會忽略註解與字串中的 FROM／JOIN，且只在實際用到資料表時載入欄位，避免大型 schema 開啟查詢視窗時一次抓取所有欄位。
- 資料表設定檔的篩選欄位只接受單一 WHERE 條件，拒絕分號、註解與頂層排序／集合子句；分頁排序會以 Primary Key 當穩定 tie-breaker，切換前也會先擋住尚未儲存的資料列變更。
- 物件 URI 只保存連線顯示名稱、database、物件類型與名稱，不包含主機、帳號或密碼；啟動解析會拒絕未知／重複／缺漏參數、帳密、額外路徑與非法百分比編碼。

## [1.0.0.17] - 2026-08-25

### 🚀 新增功能

- **右側窗格收合列**：AI 助理預設開啟，AI 助理與物件詳細資料都能暫時收合到右側圖示列，需要時再從圖示列或「檢視」選單展開。
- **視覺化執行計畫**：查詢視窗新增 MySQL／MariaDB 與 PostgreSQL 的唯讀 JSON 執行計畫，可切換樹狀視圖、節點屬性、原始 JSON 與文字計畫，並標示相對高成本操作。

### 🛠️ 問題修正與優化

- 關閉 AI 助理後會記住使用者選擇；暫時收合則不會改動偏好，下次啟動仍依原本的開啟或關閉設定顯示。
- 執行計畫一次只接受一個 SELECT／WITH／DML statement，拒絕 DDL；PostgreSQL 明確使用 `ANALYZE FALSE`，分析 UPDATE／DELETE 時不會真的修改資料。
- AI CLI 改在 mySQLPunk 專屬空白工作目錄執行；Codex 會對該目錄套用單次 trusted project 設定，Gemini 使用 session trust，避免共用暫存目錄的未信任路徑錯誤，同時不關閉唯讀沙箱。

## [1.0.0.16] - 2026-08-25

### 🚀 新增功能

- **資料分析工作區**：資料表右鍵新增五種 provider 共用的欄位分析，顯示 NULL、相異值、極值、數值平均與 Top 10 比例；預設抽樣 10,000 筆，也可切換全表，並能從分佈值直接開啟對應 WHERE 查詢。
- **小修改累積、大更新立即發布**：master 的功能與修正依分數累積，達門檻或七天批次時間才自動建立 Release；大型更新可在單一 commit 加上 `Release-Now: true` 直接發布。

### 🛠️ 問題修正與優化

- AI 助理右側的設定、關閉與重新載入模型按鈕改用向量圖示，不再因窄按鈕內距或 DPI 縮放顯示成被截斷的省略號。
- README 與「說明 > 關於」的作者名單移除 Codex 協作標示，只保留實際作者。
- Codex CLI 的模型建議改為目前可用的 GPT-5.6 系列；舊設定若仍是已退場的 gpt-5.1-codex，會自動改用 CLI 預設模型，不再送出後才被 ChatGPT 訂閱拒絕。
- CLI 執行改為先解析實際的 `.exe`／`.cmd` 路徑再啟動，避免偵測得到但呼叫時找不到；批次檔統一切到 UTF-8，Windows 中文錯誤不再顯示成 `�`。

## [1.0.0.15] - 2026-08-25

### 🚀 新增功能

- **CLI 模式可選模型**：面板與選項的模型下拉內建各家 CLI 的常用型號——Codex（gpt-5.1-codex-max／gpt-5.1-codex／mini／gpt-5.1）、Claude Code（sonnet／opus／haiku）、Gemini（2.5-pro／2.5-flash）；留空仍用該 CLI 的預設模型，也可自行輸入其它型號。按 ↻ 會重新帶入清單。

### 🛠️ 問題修正與優化

- 本版以新增功能為主，無獨立修正項目。

## [1.0.0.14] - 2026-08-25

### 🚀 新增功能

- **CLI 後端穩定性修正**：錯誤訊息去亂碼、codex 舊版旗標自動降階重試。

### 🛠️ 問題修正與優化

- CLI 輸出裡的 ANSI 色碼與控制字元現在會先洗掉：之前失敗時整屏亂碼，真正的錯誤原因反而看不到。
- codex 各版本支援的旗標不一：遇到「旗標不認得」類的用法錯誤會自動降階重試（先拿掉 read-only sandbox，再拿掉 skip-git-repo-check）。
- CLI 供應商按 ↻ 拉模型清單時，改顯示灰色說明而不是紅色「請求失敗」（那本來就不是錯誤）。

## [1.0.0.13] - 2026-08-25

### 🚀 新增功能

- **AI 助理支援本機 CLI（免 API 費用）**：新增 Codex CLI（ChatGPT 訂閱）、Claude Code CLI（Claude 訂閱）與 Gemini CLI（Google 帳號）三個供應商——直接呼叫你本機已登入的 CLI，走訂閱額度不另計 API 費；不需金鑰，選了就能用。「測試連線」會確認 CLI 已安裝並顯示版本。
- 下載即用的自動偵測：還沒設定金鑰的使用者開啟 AI 面板時，會自動掃描本機已安裝的 CLI（Codex／Claude Code／Gemini）並直接選用；選項的「偵測本機服務」也改為同時掃 CLI 與 Ollama／LM Studio，列出全部找到的選項。

### 🛠️ 問題修正與優化

- 本版以新增功能為主，無獨立修正項目。

## [1.0.0.12] - 2026-08-25

### 🚀 新增功能

- **選項補回 AI 分類**：修正選項視窗導覽清單沒有「AI」分類的問題。

### 🛠️ 問題修正與優化

- 1.0.0.10／1.0.0.11 的 AI 設定頁（供應商、金鑰、偵測本機服務、OpenRouter 一鍵授權）實際上進不去——頁面寫好了但分類沒掉進導覽清單；現在「選項 > AI」真的選得到了。

## [1.0.0.11] - 2026-08-25

### 🚀 新增功能

- **一鍵瀏覽器授權連結 OpenRouter**：選項 > AI 新增 CLI 式的瀏覽器授權（官方 PKCE 流程）——按下按鈕開瀏覽器、在網頁上同意，金鑰就自動存進 Windows 認證管理員並切換到 OpenRouter，不用手動複製貼上；一把鑰匙可用 OpenAI／Claude／Gemini 等各家模型。其它模型商不開放第三方桌面程式 OAuth，仍需手動貼金鑰。
- 支援 OpenAI codex 系列模型（gpt-5-codex／gpt-5.1-codex 等）：這些模型只支援 Responses API，程式偵測到模型名含 codex 會自動改走對應介面，設定上照樣選 OpenAI、貼 API 金鑰、模型填 codex 型號即可。

### 🛠️ 問題修正與優化

- AI 助理沒設定金鑰時，送出與拉模型清單改為先擋下並給指引，不再真的打 API 換回原始錯誤；被擋下時輸入內容保留不清空。
- 相同的系統提示與錯誤訊息不再重複洗版（連按 ↻ 或連續切換供應商只顯示一則）；修正系統訊息下方多出一截空白的問題。
- 修正左側連線清單「關閉連線」後引擎圖示沒有變回灰色的問題。

## [1.0.0.10] - 2026-08-25

### 🚀 新增功能

- **AI 助理支援十家服務與本機模型**：Punky 面板的串接從三種擴充到 OpenAI、Anthropic Claude（原生 API）、Google Gemini、Azure OpenAI、OpenRouter、Groq、DeepSeek、xAI Grok、Ollama、LM Studio 與自訂端點，使用者訂閱哪家就接哪家。
- 面板頂端新增供應商／模型快速切換列：換服務不用進選項；↻ 會跟服務端抓回可用模型清單放進下拉。
- 選項 > AI 新增「偵測本機服務」（自動找 Ollama / LM Studio 並帶入模型）與「測試連線並列出模型」；每家服務的金鑰各自存 Windows 認證管理員，並提供「前往取得金鑰／認證頁面」一鍵開啟該服務的申請網頁。
- 對話改成泡泡呈現：使用者訊息藍底靠右、Punky 回覆灰底靠左並標示使用的模型，回覆裡的 SQL 用等寬字型的獨立區塊顯示，錯誤與系統訊息也各有樣式；泡泡右鍵可複製整則內容。

### 🛠️ 問題修正與優化

- 修正 AI 服務預設值殘留舊設定「none」導致面板供應商下拉空白的問題。

## [1.0.0.9] - 2026-08-25

### 🚀 新增功能

- **Punky AI 助理與 Navicat 對齊第一波**：新增 AI 助理右側面板、資料字典、釘選查詢結果、連線搜尋與專注模式。
- AI 助理「Punky 崩琦」：檢視選單開啟右側聊天面板，走 OpenAI 相容 API（OpenAI／本機 Ollama／自訂端點，GitHub Models 因官方退場僅保留相容選項）；可附上目前連線的資料庫結構當上下文，回覆裡的 SQL 一鍵插入查詢分頁；API 金鑰存 Windows 認證管理員，不落地設定檔。
- 資料字典：資料庫節點右鍵「產生資料字典」，把資料表／檢視的欄位、索引與 CREATE 語句輸出成一份帶目錄的 HTML 文件，用瀏覽器「列印 > 另存 PDF」即可出 PDF，五種引擎共用。
- 釘選查詢結果：查詢視窗工具列的 📌 把目前結果存成唯讀快照分頁，跑新查詢也能對照比較；分頁用滑鼠中鍵或右鍵關閉。
- 連線清單搜尋：樹狀清單上方新增搜尋框，依連線名稱或群組即時篩選，Esc 清空。
- 專注模式：F11 或「檢視 > 專注模式」一鍵隱藏主功能列、導覽面板與資訊窗格，只留工作區。
- 新增 docs/ROADMAP.md：Navicat 17 亮點與 Premium 功能的對照表（已有／本輪／排程／不做），之後照這份逐輪推進。

### 🛠️ 問題修正與優化

- 修正左側連線清單的群組資料夾「打開後收不起來」的問題：雙擊資料夾時程式多切了一次展開狀態，跟 TreeView 原生的雙擊切換互相抵銷；根層資料夾也補回展開／收合箭頭（之前只有子層資料夾有箭頭）。
- 「關於」視窗作者資訊裡的網址改成可點擊，直接開瀏覽器。

## [1.0.0.8] - 2026-08-25

### 🚀 新增功能

- **Punky 品牌化與穩定性大修**：應用程式圖示換成看板娘 Punky、主功能列與連線清單圖示全面換新，並修掉全專案掃描找到的更新流程、群組拖曳、連線測試等多項問題。
- **看板娘與關於視窗動畫**：README 加入看板娘 Punky 崩琦，程式「說明 > 關於」改為自訂視窗並播放去背眨眼動畫。
- 連線清單的引擎圖示改為官方剪影風格：品牌色圓角底加白色圖形（MySQL 海豚、PostgreSQL 大象、Oracle 圓環、SQLite 羽毛、SQL Server 資料庫圓柱），未連線改灰底呈現；群組資料夾換成實體琥珀色圖示。
- 應用程式圖示全面換成看板娘 Punky 崩琦：exe、所有視窗標題列與工作列、安裝程式、桌面捷徑、.sql 檔案關聯圖示一體適用；小尺寸（16–32px）用臉部特寫、大尺寸用完整貼紙，縮小也認得出來。
- 主功能列的 12 個入口改用專屬彩色雙色向量圖示：連線、新增查詢、資料表、檢視、函式、使用者、其它、查詢、備份、事件、模型與 BI 各自呈現用途，並支援亮色、暗色、停用與選取狀態自動換色。

### 🛠️ 問題修正與優化

- 將 MP4 素材改以約 16 fps 的透明 GIF 打包進程式輸出，讓關於視窗播放更流暢，也避免一般 MP4 不支援透明 alpha 與 WinForms 影片播放相容性問題。
- 修正預設按鈕（如「確定」「下一步」）四個角落出現方形邊框殘影的問題：圓角外的區域改請系統畫入真實的父容器背景，系統補畫的預設按鈕邊框也改成與背景同色。
- 修正 PostgreSQL 查詢逾時被寫死 8 秒的問題：超過 8 秒的大表瀏覽、匯出一律逾時失敗，現在改用 Npgsql 預設的 30 秒，逾時調整也真正套用到每個查詢。
- MySQL 連線改為「伺服器支援就走 TLS 加密」（Preferred），不支援的舊伺服器自動退回，既有連線不受影響；以前寫死不加密。
- 啟動自動檢查更新：按「立即更新」後的下載或驗證失敗現在會跳出錯誤訊息（以前只寫進狀態列，讓人以為更新正在進行）；按「稍後再說」會記住該版本，下次啟動不再重複跳同一版的提示，出新版或手動檢查照常提醒。
- 連線樹狀清單重繪（建群組、搬連線、編輯連線等）不再把收合的群組全部重新展開，選取的節點也會保留；啟動時展開中的群組資料夾圖示不再誤顯示為閉合狀態。
- 修正「隱藏連線群組」檢視下拖曳連線會悄悄清空其群組歸屬的問題：隱藏模式下不啟動分組拖曳。
- 「移至群組」輸入 `A/`、`/A` 這類字串不再產生空白名稱的鬼群組；群組改名撞到既有名稱時不再於設定檔留下重複項；拖曳高亮不再因大小寫誤亮到別的群組。
- 五個資料庫的「測試連線」改為背景執行：連不上時視窗不再整個凍住，測試期間按鈕會先停用避免連點重複觸發；MySQL/PostgreSQL 測試連線的資源也會正確釋放。
- SQLite 連線字串改用 builder 組裝，資料庫路徑含分號不再解析錯誤。

## [1.0.0.7] - 2026-08-24

### 🚀 新增功能

- **巢狀群組與全新引擎圖示**：連線群組支援多層資料夾、整個資料夾可以拖曳搬家，引擎圖示改為品牌色 chip 一眼可辨。
- 連線群組支援多層巢狀：資料夾裡可以再放資料夾（群組名稱用「/」分層，例如「正式站/北區」）；群組節點右鍵多了「新增子群組」，改名與刪除會連動所有子群組與其中的連線。
- 群組資料夾本身也能拖曳：整個資料夾（含子群組與連線）拖進其它群組或拖回頂層，路徑自動改寫。
- 連線清單的引擎圖示重新設計：品牌色圓角 chip 加白色記號（橘 M＝MySQL、藍 P＝PostgreSQL、紅 O＝Oracle、紫 S＝SQL Server、藍底羽毛＝SQLite），五種資料庫一眼可辨，未連線降彩度顯示。

### 🛠️ 問題修正與優化

- 本版以新增功能為主，無獨立修正項目。

## [1.0.0.6] - 2026-08-24

### 🚀 新增功能

- **拖曳分組、啟動更新提示與介面打磨**：連線清單可拖曳進出群組、啟動偵測到新版會跳正式更新提示視窗、連線編輯視窗輸入框圓角化、新增連線精靈換頁不再跳走。
- 左側連線清單支援拖曳分組：把連線拖到群組資料夾就移入、拖到空白處就移出，拖曳時目標群組會亮起提示；原本的右鍵「移至群組／移出群組」照常可用。
- 啟動時偵測到新版本會跳出正式的更新提示視窗：顯示版本資訊與更新內容，可一鍵「立即更新」（沿用下載＋SHA-256 校驗＋啟動安裝流程）、查看發行頁或稍後再說；手動檢查更新也用同一個視窗。

### 🛠️ 問題修正與優化

- 連線編輯視窗的輸入框改為圓角外框、加了內距，聚焦時邊框亮主題色，停用時灰底，深淺主題都跟著設計系統走。
- 修正「選取連線類型」按下一步時整個視窗被關掉、編輯視窗又從別處冒出來的問題：下一步改為同一個視窗換頁，並把視窗縮放成編輯頁大小、維持中心點不動，操作起來才有精靈換頁的接續感；Enter／Esc 也直接對應編輯頁的確定／取消。

## [1.0.0.5] - 2026-08-24

### 🚀 新增功能

- **連線視窗改版與大規模掃修**：五個資料庫的連線編輯視窗全面改版、樹狀圖示改為向量繪製並加上引擎形狀徽章，並修正多輪掃修累積的大量問題（設定檔與憑證安全、備份匯出、SQL 編輯器、啟動流程等）。
- 「開啟方式」註冊的 .sql 檔案現在真的會被開啟：連線後自動載入到新的查詢分頁（原本註冊了但主程式完全忽略檔案參數）。
- 「允許重複執行 mySQLPunk」選項正式生效：關閉時強制單一實例（原本選項無作用）。
- 查詢歷史雙擊會先驗證資料庫一致，屬於其他資料庫的紀錄會提示先切換，不會把 SQL 開錯庫。
- 主功能列「自動執行」改名為「事件」以符合實際內容（事件/觸發器檢視）。
- 連線節點右鍵新增「重新連線」與「關閉連線」：逾時或休眠斷線後一鍵重連，不用再從檔案選單關掉重點。
- 引擎圖示加上形狀徽章（MySQL 圓、PostgreSQL 方、Oracle 三角、SQLite 菱形、SQL Server 橫槓）：顏色＋形狀雙重編碼，色弱使用者也能分辨。

### 🛠️ 問題修正與優化

#### 第六輪掃修（角落功能與第五輪修法的對抗性審查）

- 文件整理：README 精簡成功能概況與已知限制，歷史完成紀錄搬到 docs/FEATURE_NOTES.md；新增 CONTRIBUTING.md 說明開發、測試、CHANGELOG、發版與文件維護規範；補上 GitHub 專案描述、topics 與 exe 檔案描述。

- 對第五輪修法的回歸修正：
  - 「允許重複執行」預設改回允許（多開是長期既有行為，上一輪不小心把預設關掉，升級後會突然不能開第二個視窗）；帶 .sql 檔啟動時一律放行，否則檔案永遠開不起來。
  - 關閉連線／匯入設定前關分頁改走正常的關閉流程：設計器有未儲存變更會照常詢問要不要存，另外先統計未存分頁數量供確認，按取消就中止整個操作（原本直接 Dispose，未存的變更無聲蒸發）。
  - 美化對含 `#` 的 SQL 一律略過（`#tmp` 暫存表、PG `#>` 運算子之外還有太多情境，格式器一律處理不了）。
  - 刪除設定檔清憑證前先驗證憑證確實屬於該設定檔（原本可能誤刪其他設定檔共用位址的密碼）；複製設定檔改寫憑證前先正規化連線欄位，跟儲存時的鍵值算法一致。
  - 設定檔解析失敗後鎖住寫入（原本提示歸提示，下一次存檔還是會把損毀前的資料蓋掉）；損毀備份檔名固定，不會越積越多。
  - 匯入連線期間暫停憑證寫入，匯入完成才解鎖（原本匯入途中就開始寫認證管理員，取消匯入會留下孤兒憑證）。
  - 查詢執行的 busy 旗標與按鈕狀態全部移進 try/finally（原本前置檢查丟例外會讓查詢按鈕永久卡住）。
  - 群組節點選取加入重入保護與例外處理；扁平清單備援只在「隱藏物件群組」等模式下啟用（原本正常模式下也可能走進去顯示錯的清單）。
- SQLite：關閉或切換連線會重設 SpatiaLite 載入狀態（原本換檔後空間功能停留在前一個檔案的狀態）。
- 匯出：XLSX 會擋掉超過格式上限的列數/欄數並明講（原本產出 Excel 打不開的壞檔）；XML 匯出剔除非法控制字元。
- 匯入 SQL 檔案：編碼偵測改為嚴格 UTF-8 失敗才退回系統編碼（原本 Big5 檔案直接變亂碼）。
- 更新腳本補上 BOM（PowerShell 5.1 把無 BOM 的 UTF-8 當 ANSI 讀，含中文路徑時更新會失敗）。
- 進度遮罩顯示期間鎖住主視窗（原本還能點到底下的按鈕觸發第二個操作）；移除強制訊息迴圈，避免重入。
- 遠端鏡像備份：目的地比對忽略結尾斜線差異（原本 `D:\backup` 和 `D:\backup\` 被當成不同位置又拷一份）。

#### 第五輪掃修（設定檔安全、設計器、以及前一輪修法的回歸修正）

- 設定檔與憑證安全：
  - 複製連線設定檔不再沿用來源的憑證參照（原本切換後任何一次存檔都會把「來源設定檔」的所有密碼從 Windows 認證管理員刪掉）；刪除設定檔會一併清掉其憑證。
  - `setting.ini` 改為原子寫入（原本先截斷再寫，中途當機整份連線就毀了）；解析失敗時備份損毀檔並明確告知，不再默默用空清單覆蓋。
  - 設定檔路徑改跟程式目錄（原本跟工作目錄，從檔案總管開 .sql 啟動時連線清單會整個消失）。
  - 發佈打包排除 `setting.ini`／`connection_profiles` 等本機設定（原本會把真實連線設定包進安裝檔）。
- 資料表設計師：索引方法欄位對 MSSQL/Oracle/PG 的值不再直接炸例外；儲存不再蓋掉手動修改的預覽 SQL；SQLite 內部索引（sqlite_autoindex）不再導致重建失敗；Oracle 只改型別不再因重申 NULL/NOT NULL 而失敗、清空預設值會真的清掉；MySQL 只改小數位/UNSIGNED 也會偵測到變更；主鍵欄位隱含 NOT NULL；刪除欄位後上移/下移/插入不再操作到錯的列。
- 關閉連線／切換設定檔會連帶關閉使用該連線的查詢與設計分頁（原本分頁留著、按執行就打到已釋放的連線）。
- 主功能列區段按鈕在資料庫尚未展開時會等載入完成（原本前兩次點擊沒反應）；開啟「隱藏物件群組」後區段按鈕改為直接顯示該群組清單（原本永久失效）。
- 檢查更新：GitHub 回應改用 UTF-8 解碼（release 名稱含中文時原本會解析失敗）。
- 選項視窗移除 17 個「有介面但從未生效」的假選項（起始畫面、AI 助理頁、編輯器行號等），保留的都是真的有作用的。
- 回歸修正（對前一輪修法的對抗性審查）：切句器的語句邊界把註解視為空白（DELIMITER 前有註解不再讓整份腳本切壞，並補上專屬測試）；美化的 `#`／`\` 規則綁定方言（MSSQL 暫存表、PG jsonb 運算子不再被誤判），略過時會在狀態列說明；查詢 busy 旗標移回 try/finally 保護範圍；我的最愛對斷線後的佔位節點、連線失敗與樹重建都能正確處理；停靠提示不再推擠版面造成閃爍；隱藏欄位不再參與清單篩選；筆數框失焦不再靜默改寫全域選項。

#### 檢查更新修正

- 修正「檢查更新」在部分環境直接失敗的問題：GitHub API 要求 TLS 1.2 以上，但 .NET Framework 在某些機器上的預設 TLS 交涉不起來，出現「無法建立 SSL/TLS 的安全通道」。現在啟動時明確啟用 TLS 1.2 / 1.3，檢查更新、下載安裝檔與補註解字典下載都走得通。

#### 連線編輯視窗改版

- 五個 provider 的連線編輯視窗（MySQL / PostgreSQL / Oracle / SQLite / SQL Server）全面改版：共用同一套版面——標題列有引擎色圖示、欄位用格線對齊、按鈕列置底（確定為主要按鈕），字型跟上整體設計系統，不再是新細明體加手排座標；拿掉只有一頁的「一般」分頁，Enter / Esc 直接對應確定 / 取消。Oracle 的 Basic / TNS 兩種模式欄位也對齊同一套格線。

#### 圖示與文字尺寸調整

- 樹狀清單圖示全面改為向量繪製：五種資料庫引擎各有專屬色（MySQL 橘、PostgreSQL 藍、Oracle 紅、SQLite 亮藍、SQL Server 紫），未連線以降彩度呈現、連線後亮起；群組圖示（資料表/檢視/函式/事件/查詢/報表/備份/使用者/模型/BI/其它）與資料夾也換成同一套設計語言，不再依賴 image/ 下的舊點陣圖，任何 DPI 都清晰。
- 新增報表（文件）、展開資料夾、上移/下移箭頭圖示；資料夾改為圓角含籤片的現代造型；插頭補上纜線弧線。
- 資料表設計師工具列的 PNG 圖示（儲存/執行/新增/插入/刪除欄位、上移/下移、自動註解）全部換成向量。
- 尺寸校正：主功能列圖示 40px → 32px（大型工具列常見預設為 24–32px，40 偏大）；查詢視窗工具列圖示保留區對齊實際的 16px；物件工具列與樹狀維持標準 16px。
- 文字尺寸：內文 9pt → 9.5pt、小字 8.25 → 8.5、副標 10 → 10.5、標題 11.5 → 12（WinForms 預設為 Segoe UI 9pt，但繁中在 9pt 偏小；現代桌面工具內文約 13px ≈ 9.75pt）。
- 修正每次重畫樹狀清單就洩漏一組 ImageList 的問題。

#### 第三輪掃修（編輯器、我的最愛、匯入合併、以及前兩輪修法的回歸修正）

- SQL 編輯器：
  - 「美化」不再破壞含註解或反斜線跳脫的 SQL（原本 `--` 會被拆成兩個減號、註解文字混進語句變成會執行出錯的東西）；改用可 Ctrl+Z 復原的取代方式。
  - 自動完成按 Enter/Tab 不再多插入換行或 Tab；語法上色加上例外防護（原本一出錯之後打字就永遠不再上色）並修掉每鍵數十個字型物件的洩漏。
  - BLOB 匯入加上例外處理與大檔確認；BLOB 存檔在串流失敗回退時會講明寫出的是畫面上的值。
- 主視窗：
  - 修正在查詢歷史、連線診斷這類清單上按右鍵直接未捕捉例外。
  - 匯入連線用「合併」不再把既有連線的 Windows 憑證密碼刪掉（原本合併後密碼無聲消失）。
  - 我的最愛整組修好：切換語言後仍找得到（原本存本地化路徑、換語言全部失效）、未連線時會先連線並等載入完成（原本第一次點永遠「找不到」）、啟用連線群組時層級不再錯位、不再重複觸發選取造成查兩遍。
  - 查詢歷史：雙擊可用完整 SQL 開新查詢分頁（原本只存 120 字預覽且雙擊沒反應）。
  - 使用者清單的「選擇欄位」偏好改用實際欄名（原本設定完全無效）；欄位選擇器按取消真的會取消（原本切個分類就已寫進設定檔）。
  - 主題切換後主功能列目前區段的高亮不再消失；浮動視窗的分頁釋放，反覆浮出停靠不再累積視窗 handle。
- 服務層：
  - SQLite 欄位註解匯入包進交易（原本先整表刪除再逐筆插入、中途失敗原註解全滅且無提示）；YAML 匯出補換行跳脫（多行註解可正確往返）。
  - 可攜版更新腳本改帶 BOM（原本中文使用者名稱/路徑下 PowerShell 讀成亂碼、更新必失敗）。
  - MySQL 使用者管理：欄位層級授權不再解析出非法權限名稱；刪除/改名帳號在取不到 Host 時擋下而不是猜 %（可能打到別的帳號）。
  - RTree 精靈允許合法的 1 維索引、不再覆寫使用者輸入的名稱。
- 前兩輪修法的回歸修正（對抗性自我審查）：
  - 資料分頁執行帶 WHERE/ORDER BY 的單表查詢恢復可編輯（上一輪把它鎖成唯讀是矯枉過正；寫回本來就是逐列主鍵比對）。
  - 備份暫存檔在「驗證通過但取代失敗」時保留（是僅存的完好備份）；暫存檔不再被完整性排程誤掃成備份；隔離還原改用 File.Replace 消除失敗窗口。
  - Oracle View 改名先切換 CURRENT_SCHEMA（避免改到自己 schema 的同名物件）；PostgreSQL 索引查詢相容 PG 10 以下。
  - 切句器不再把名為 delimiter 的欄位定義行當成指令吃掉；頁籤拖曳回復原始行為（上一輪的「修正」反而讓頁籤永遠拖不到最右邊）；還原 CSV 匯出對歐陸語系負數的誤加引號；超大 BLOB 匯出加上 16MB 完整輸出上限避免 OOM。
- 其他：include.cs 的 JSON 解析與 https 下載修正；CSV/TSV 匯出統一帶 BOM（Excel 開啟中文不再亂碼）、JSON/XML 統一不帶；SQLite 連線對話框對手動輸入的不存在路徑先確認再建新檔。

#### 第二輪掃修與功能補完

- 資料庫層修正：
  - PostgreSQL 查詢不再把整段 SQL 的 `@` 換成 `:`（`'abc@gmail.com'` 會被改成錯值、jsonb 的 `@>` 會語法錯誤）。
  - Oracle：資料表清單的 `AS ROWS`、複製欄位的 `AS COMMENT` 是保留字會直接 ORA 錯誤，已加引號；View 改名不再帶 schema 限定名（原本必失敗）；連線設定的 service_name/SID/TNS 等欄位現在會存檔（原本重開程式後 Oracle 連線全數失效）。
  - SQL Server：讀取 View DDL 前先切換資料庫（原本連在 master 時會拿到空白或別的物件定義）；索引查詢排除 INCLUDE 欄位。
  - 所有 provider 的資料分頁改為依主鍵排序，翻頁、匯出、複製不再有重複或漏列的風險。
  - MySQL SRID=0 的 geometry 不再被解析成錯誤座標（WKB 解析加上完整長度驗證）。
- 備份／匯出／還原：
  - 還原的切句器看懂 MySQL dump 的 `'` 跳脫與 `DELIMITER` 指令（原本自家備份含單引號資料或 stored procedure 時還原必失敗且留半套）。
  - 匯出的 DATETIME 一律用 invariant 格式（原本跟著系統文化走，民國曆設定下產出的 dump 無法還原）；TIME 欄位補引號；MySQL 字串補反斜線跳脫；浮點數用完整精度。
  - 備份先寫暫存檔、驗證通過才取代舊檔（原本先刪舊檔，失敗會兩頭空）；隔離區還原同樣改為安全順序；還原失敗的訊息會附上事前安全備份的路徑。
  - SQLite 改名連同 -wal/-shm 一起搬移，WAL 中未 checkpoint 的交易不再遺失。
  - CSV/TSV 匯出對 `=`、`+`、`@` 開頭的值加公式注入防護；BLOB 匯出改為完整十六進位（原本只寫入畫面預覽字串，資料靜默遺失）。
  - 大檔 SQL 匯入的切句從 O(n²) 改為線性。
- 連線與憑證：
  - 密碼憑證改為「先寫新的、成功才刪舊的」（原本 Credential Manager 寫入失敗會讓密碼無聲消失）；解密加上回寫驗證，root/user 這類明文舊帳號不再被解成亂碼。
  - MySQL 連線對話框新增「初始資料庫」欄位；MySQL/PostgreSQL 的測試連線改用 ConnectionStringBuilder（密碼含分號不再誤判失敗），MySQL 測試不再硬連 mysql 系統庫。
  - Proxy 未啟用時沿用系統設定（原本會被設成「完全不用 proxy」）；port 打錯字時回退預設值而不是 0。
- 功能補完：
  - 複製資料表帶主鍵：五種資料庫的複製建表都會帶上 PRIMARY KEY（原本除 MySQL 外全部遺失，複製出來的表開資料分頁會變唯讀）；複製中途失敗會清掉半套目標表；MySQL 索引複製支援 FULLTEXT/SPATIAL 與前綴長度。
  - PostgreSQL 的 DDL 補完：包含主鍵、DEFAULT、欄位/資料表註解與索引（原本只有欄位加型別）。
  - SQLite 的 FTS5/RTree 虛擬表現在會列在資料表清單與樹狀（引擎欄標示模組名），模組未編入時自動略過避免連環錯誤。
  - 「Provider 能力」報表依實際實作回報（使用者管理僅 MySQL、Events 僅 MySQL、Oracle 無資料庫改名等），不再一律顯示「支援」。
  - MySQL 的 `sys_` 開頭資料表不再被誤當系統物件藏起來。

#### 介面改版

- 新增 `UiKit` / `UiControls` 設計系統：色彩、間距、圓角、字級 token，以及純程式繪製的向量圖示與區塊標題列、分段控制項、空狀態等共用元件。
- `ThemeManager` 重寫為單一樣式來源，工具列、功能表、按鈕、輸入框、分頁、樹狀與資料表格改為自訂繪製，淺色與深色主題共用同一組 token。
- 主功能列與物件工具列改用向量圖示，移除 resx 內的預設佔位圖（原本顯示為破圖），選取狀態由生硬藍色方框改為柔和圓角底加強調色底線。
- 樹狀清單改為全自繪：移除虛線連接線與焦點虛線框，加入整列圓角選取、滑鼠停留回饋與自訂展開箭號。
- 「連線」與「物件詳細資料」面板加上一致的區塊標題列，側欄的資訊／DDL 切換改為分段控制項。
- 樹狀、內容區與側欄在沒有資料時顯示空狀態說明，狀態列右側新增連線數摘要。
- 選取連線類型視窗改用停駐排版，修正搜尋框與檢視切換鈕被推出可視範圍的問題，並替卡片加上滑鼠停留回饋。
- 選項視窗左側導覽改為圓角分頁樣式，動作列加上分隔線與主要按鈕強調。
- 查詢視窗工具列與資料列工具列改用向量圖示，取代原本的文字符號。

#### 修正（本輪錯誤掃修）

- 資料表資料分頁：儲存目標改以實際載入資料的 SQL 為準。原本是抓編輯器當下文字，先改文字再按儲存會把修改寫進別張資料表。
- 資料表資料分頁：執行 JOIN 或非單表查詢後結果自動鎖唯讀；主鍵欄位不在結果集或主鍵值被修改時，儲存會明確報錯而不是默默出錯或假裝成功。
- 查詢視窗：F5／Ctrl+Enter 在查詢執行中不再重入（同一條連線同時跑兩個查詢會互咬，取消鈕也會失效）。
- 資料表設計師（MySQL）：修正 `int(10) unsigned` 的 unsigned 被塞進長度、`enum('a','b','c')` 被逗號切爛只剩前兩個值的問題；ALTER 路徑的 DEFAULT 改用與 CREATE 相同的跳脫邏輯，`CURRENT_TIMESTAMP` 與含引號的預設值不再產生錯誤 SQL。
- SQLite：修正把 `docs_archive` 這類正常資料表誤判為 FTS 影子表而藏起來的問題（改為只認已知影子表後綴）；欄位註解讀取失敗時不再清空全部註解（原本會在儲存時把空註解寫回）。
- 樹狀清單：群組節點「重新整理」與複製貼上後改為等載入完成再選取節點（原本會遍歷剛清空的樹，選取永遠失效）；雙擊 Tables/Views 群組節點不再讓狀態列卡在「正在載入資料...」。
- 頁籤：往右拖曳排序不再多跑一格；浮動視窗停靠回主視窗時若有未儲存的變更，不再直接銷毀（原本同名分頁存在時會連未存的編輯一起消失）。
- 靜默失敗浮出：資料表清單載入失敗會清空清單並在狀態列說明（原本會留著上一個資料庫的清單）；選項視窗存檔失敗會跳提示（原本重開程式設定就消失）；自動備份草稿寫檔失敗不再讓程式當掉，且草稿檔名加入分頁標題、同庫多分頁不再互相覆蓋。
- PostgreSQL：連線改用連線設定裡填的初始資料庫（原本寫死連 `postgres`，帳號無權限時直接失敗）。
- 介面框架：修正 `SetGlyph` 會釋放 `Properties.Resources` 共用圖片導致點「使用者」分類時當掉；移除切換主題時逐列逐格設定樣式造成大結果集卡死的程式碼；控制項停用/唯讀狀態改變後顏色會跟著更新；樹狀停留改為只重畫該列減少閃爍；修掉多處事件重複訂閱與 GDI 資源未釋放。
- 移除未被編譯的空殼檔 `lib/oracle_add_edit.cs`。

#### 修正

- 修正 `ThemeManager` 對 `ToolStripTextBox` / `ToolStripComboBox` 套用透明背景時會丟出「控制項不支援透明的背景色彩」例外。
- 側欄圖示改為程式繪製，不再以 `Image.FromFile` 長期鎖住 `image/` 下的檔案。

#### 驗證

- Windows Release 建置與 47 項 SmokeTests 通過。
- 以 SQLite 測試資料庫實際驗證主視窗、連線精靈、選項視窗、資料表資料視窗在淺色與深色主題下的呈現。

## [1.0.0.4] - 2026-08-20

### 🚀 新增功能

- **單一 EXE 安裝與安全更新**：
  - GitHub Release 只提供一個 `mySQLPunk-1.0.0.4-win-x64-setup.exe`，不再要求使用者另外下載 portable ZIP 或 manifest。
  - Inno Setup 安裝程式已包含 managed DLL、SQLite／SpatiaLite 原生 runtime、素材與第三方授權檔。
  - 程式內更新會讀取 GitHub Release asset 的 SHA-256 digest，下載完成並通過校驗後才啟動安裝程式。
- **完整資料庫匯入、匯出與改名**：
  - MySQL／MariaDB 完整匯出與匯入支援 Table、View、Function、Procedure、Trigger、資料、索引、外鍵、註解與大型檔案串流處理。
  - Database Tree 支援 F2／右鍵改名，並完成 MySQL／MariaDB、SQLite、PostgreSQL 與 SQL Server 的實際資料庫改名流程。
- **MySQL／MariaDB 使用者管理**：
  - 完成使用者、角色、權限與資源限制管理，並持續補齊檢視選單、選項中心、暗色主題及繁中／英文語系。

### 🛠️ 問題修正與優化

- **發版資產防護**：重跑同版本 workflow 時會清除殘留的舊資產，並拒絕發布零個或多個 setup EXE，避免下載頁同時出現互相衝突的套件。
- **跨版本資料庫驗證**：MySQL 5.6、5.7、8.0 與 MariaDB 10.6、10.11、11.4 的匯出、匯入及複製改名整合測試均已通過。
- **跨資料庫改名驗證**：SQLite、PostgreSQL 17 與 SQL Server 2022 的資料庫改名整合測試均已通過。
- **安裝流程驗證**：Windows Release 建置與 47 項 SmokeTests 通過；單一 EXE 已完成靜默安裝、必要 runtime 檢查、主視窗啟動、關閉與解除安裝測試。

## [1.0.0.3] - 2026-06-11

### Release Highlights

- Prepared the next public release after the `v1.0.0.2` compliance pass.
- Rebuilt and verified the SpatiaLite runtime package metadata, including `built_at_utc`, `build_tool`, and per-file SHA-256/byte checks.
- Kept `sqlite3.exe`, `libreadline8.dll`, and `libtermcap-0.dll` out of the packaged runtime to avoid shipping the unused Readline-linked SQLite shell.
- Added smoke-test coverage for the committed SpatiaLite runtime manifest so stale hashes or blocked files are caught before release.

### Updates And Packaging

- Added portable ZIP update support that can download a GitHub Release asset, verify it with `release-manifest.json`, generate an apply script, replace the current app after exit, and restart `mySQLPunk.exe`.
- Hardened GitHub Release packaging with bundled root notices, image asset notices, native runtime notices, and NuGet license/notice files, including the Oracle Managed Data Access license.
- Improved release manifest verification for portable update assets and SpatiaLite native runtime files.
- Updated the release workflow so GitHub Release notes can be generated from this changelog instead of a placeholder body.

### Database And UI Improvements

- Added the option to hide database object groups, allowing Tables, Views, Functions, Users, Events, and Queries to appear directly under each database node.
- Improved View/Table copy and provider SQL fallback messages so unsupported or failed conversions explain the reason more consistently.
- Localized many UI, diagnostics, backup, import/export, provider, metadata, SpatiaLite, update, proxy, registration, and error fallback messages in Traditional Chinese and English.
- Normalized empty or missing exception reasons to localized unknown-error messages instead of blank dialogs.

### Notes

- This release focuses on packaging, update flow, localization, diagnostics, and third-party notice cleanup.
- The existing `v1.0.0.2` GitHub Release does not include these changes; publish this release with tag `v1.0.0.3`.
- Installer-based updates are still a future enhancement; the current release asset is the portable ZIP plus `release-manifest.json`.
