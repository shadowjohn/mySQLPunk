# mySQLPunk

[![Auto Release Gate](https://github.com/shadowjohn/mySQLPunk/actions/workflows/auto-release.yml/badge.svg)](https://github.com/shadowjohn/mySQLPunk/actions/workflows/auto-release.yml)
[![Latest release](https://img.shields.io/github/v/release/shadowjohn/mySQLPunk?display_name=tag)](https://github.com/shadowjohn/mySQLPunk/releases/latest)
[![License](https://img.shields.io/github/license/shadowjohn/mySQLPunk)](LICENSE)

> 免費開源的多資料庫 GUI、SQL 編輯器與 DBA 工作台；提供 Windows 完整版，以及 Linux / macOS 跨平台預覽版

mySQLPunk 的 Windows 完整版（WinForms）可用同一個介面連接 MySQL / MariaDB、PostgreSQL、SQL Server、SQLite / SpatiaLite、Oracle、MongoDB、Redis / Microsoft Garnet，以及 Snowflake。新的 Avalonia 跨平台預覽版可原生執行於 Linux 與 macOS，目前已提供 MySQL / MariaDB、PostgreSQL、SQL Server、SQLite 的連線管理、物件瀏覽、SQL DDL / DML / 查詢、結果網格、CSV / TSV / JSON 結果匯出，以及具 Primary Key 與競爭衝突防護的 Table 資料編輯；其餘 Windows 完整版功能正分階段遷移。

Open-source Windows database client, SQL editor, database GUI and DBA workbench for MySQL, MariaDB, PostgreSQL, SQL Server, SQLite, SpatiaLite, Oracle, MongoDB, Redis, Microsoft Garnet and Snowflake workflows.

[下載最新版](https://github.com/shadowjohn/mySQLPunk/releases/latest) · [功能概況](#目前功能概況) · [連線安全](docs/CONNECTION_SECURITY.md) · [自動執行作業](docs/AUTOMATION.md) · [開發與貢獻](CONTRIBUTING.md) · [功能路線圖](docs/ROADMAP.md)

## 常見用途

- 在 Windows 完整版管理 MySQL / MariaDB、PostgreSQL、SQL Server、SQLite / SpatiaLite、Oracle、MongoDB、Redis / Garnet 與 Snowflake 連線；或在 Linux / macOS 預覽版管理 MySQL / MariaDB、PostgreSQL、SQL Server 與 SQLite。
- 依連線設定 SSL/TLS 憑證驗證，或透過有 SHA256 主機金鑰固定的 SSH Tunnel 連到內網資料庫。
- 使用具自動完成、程式碼片段、查詢歷史、唯讀執行計畫與多格式匯出的 SQL 編輯器。
- 編輯資料列、設計資料表、產生 DDL / DML、搬移 Table / View、建立 ER 圖並比較兩個資料庫結構。
- 建立每日查詢、CSV / Excel / JSON 等格式匯出與 SQL 備份作業，交由 Windows Task Scheduler 執行並保留紀錄。
- 選用 OpenAI 相容 API、Ollama、LM Studio、Codex CLI、Claude Code CLI 或 Gemini CLI 作為 SQL 助理；選項頁會列出三種 CLI 的安裝路徑與非敏感登入帳號資訊，沒有 AI 服務也能使用其他資料庫功能。

Windows 完整版介面支援繁體中文與英文；資料庫密碼、SSH 密碼、私鑰密語與用戶端憑證密碼存在 Windows 認證管理員，不會以明文留在設定檔或自動執行作業檔。跨平台預覽版目前使用繁體中文介面，連線設定採 JSON 保存；資料庫密碼可由使用者選擇交給 Linux Secret Service 或 macOS Keychain，系統密碼庫不可用或未勾選時才只保留在程式記憶體。

<p align="center">
  <img src="snapshot/mySQLPunk_avatar.png" alt="看板娘：Punky 崩琦" width="260">
</p>
<p align="center"><strong>看板娘：Punky 崩琦</strong>，現在也會在「說明 > 關於」裡眨眼打招呼。</p>

作者：

- 羽山秋人 ( https://3wa.tw )
- [**NickYCLin**](https://github.com/NickYCLin) ([https://github.com/NickYCLin](https://github.com/NickYCLin))

## 最新版本

目前發版版本：`v1.0.0.19`，最新版請看 [GitHub Releases](https://github.com/shadowjohn/mySQLPunk/releases)。

目前的 `v1.0.0.19` GitHub Release 仍只提供 Windows 完整版；下一版起，Release workflow 會同時發布 Windows x64 setup、self-contained Linux x64／ARM64 安裝壓縮檔，以及 macOS Intel／Apple Silicon `.app.zip`。Linux 壓縮檔內附目前使用者層級的 `install.sh`／`uninstall.sh`；macOS 預覽目前採 ad-hoc 簽署、尚未 Apple notarize。每個跨平台資產都有獨立 `.sha256` 可驗證完整性。完整變更請見 `CHANGELOG.md`。

## 開發環境

### Linux / macOS 跨平台預覽版

從原始碼建置需要 .NET 8 SDK；GitHub Release 的 Linux / macOS 壓縮檔為 self-contained，不需要另外安裝 .NET。桌面 UI 使用 Avalonia，資料庫驅動使用純 managed 的 MySqlConnector、Npgsql、Microsoft.Data.SqlClient 與 Microsoft.Data.Sqlite，因此不依賴 WinForms 或 Windows SQLite interop。Linux 若要保存密碼，另需安裝提供 `secret-tool` 的 `libsecret-tools`，並使用 GNOME Keyring 或相容的 Secret Service；macOS 直接使用系統內建 Keychain。

```bash
dotnet restore mySQLPunk.CrossPlatform.sln
dotnet build mySQLPunk.CrossPlatform.sln -c Release --no-restore
dotnet run --project mySQLPunk.CrossPlatform.SmokeTests/mySQLPunk.CrossPlatform.SmokeTests.csproj -c Release --no-build
dotnet run --project mySQLPunk.Desktop/mySQLPunk.Desktop.csproj -c Release
```

若已安裝 Docker，可再跑 MySQL 8 / PostgreSQL 16 / SQL Server 2022 實機 round-trip；腳本只會建立並清理 `mysqlpunk-cross-*-test` 測試容器與測試自行建立的暫存 database：

```bash
./tests/Run-CrossPlatformLiveTests.sh
```

產生可安裝／攜帶的 self-contained 發佈資產：

```bash
./scripts/package-cross-platform.sh --version 1.0.0.19 --runtime linux-x64
./scripts/package-cross-platform.sh --version 1.0.0.19 --runtime linux-arm64
# 以下兩個命令需在 macOS 執行，才能驗證 codesign 與 .app archive metadata：
./scripts/package-cross-platform.sh --version 1.0.0.19 --runtime osx-x64
./scripts/package-cross-platform.sh --version 1.0.0.19 --runtime osx-arm64
```

Linux 資產是 `.tar.gz`，解壓後執行 `./install.sh` 即會以交易方式安裝到目前使用者的 XDG data 目錄並建立 `~/.local/bin/mysqlpunk` 與應用程式選單項目；任一步驟失敗會完整回復 app、launcher 與 desktop entry，`./uninstall.sh` 則只移除該版本程式並保留連線設定。macOS 資產是標準 `.app.zip`，首次安裝解壓後可拖到 Applications。兩個平台的已安裝版本都可從「檢查更新」下載、驗證、安全套用並重新啟動；新版若未通過啟動健康檢查會自動 rollback 與重開舊版。設定 `MYSQLPUNK_MACOS_SIGN_IDENTITY` 可用 Developer ID 簽署；另外設定已存在於 Keychain 的 `MYSQLPUNK_MACOS_NOTARY_PROFILE` 時，打包腳本才會送 Apple notarization 並 staple，沒有憑證時只做可驗證的 ad-hoc 簽署。

目前預覽版包含：連線設定與測試、資料庫選擇、Table / View metadata、物件預覽 SQL、DDL / DML / SELECT、取消執行、動態結果網格、CSV / TSV / JSON 結果匯出、Table 資料編輯，以及依目前 OS／CPU 選擇安裝資產的 GitHub Release 更新檢查與安全下載。雙擊 Table 會開啟每頁 200 列的獨立編輯器，並依 Primary Key 提供穩定的上一頁／下一頁；新增、修改與刪除均使用參數化 SQL 與單列交易，修改／刪除要求 Primary Key，並比對載入時的原始值，資料已被其他連線改動時會回復交易、要求重新整理。MySQL／MariaDB BLOB、PostgreSQL bytea、SQL Server binary／varbinary／image 與 SQLite BLOB 可用 `0x` 加偶數個 hex 字元安全編輯，最多 1 MiB；MySQL／MariaDB BIT(1–64) 可用依 bit width 限制的非負十進位整數編輯，ENUM／SET 會用原生參數並由 server 驗證宣告值，TIME 支援負值、微秒及完整 ±838:59:59 範圍，YEAR 僅接受 `0` 或 `1901–2155`，避免兩位數年份被靜默轉換，PostgreSQL bit(n)／bit varying(n) 則以保留前導零的 0／1 bit string 編輯；SQLite `date` 固定使用純 `yyyy-MM-dd`，PostgreSQL `date` 則以 canonical 文字無損支援 4713 BC–5874897 AD 與 `±infinity`，兩者都拒絕會被丟掉時間部分的 timestamp；MySQL／MariaDB DECIMAL 可保留最多 65 位、PostgreSQL NUMERIC 可保留任意宣告精度與負 scale、SQL Server DECIMAL 可保留最多 38 位，三者都以文字驗證再透過參數化顯式 CAST 寫入，拒絕超出 precision／scale 的值而不做無聲四捨五入；MySQL／MariaDB JSON、PostgreSQL json／jsonb 與 SQLite JSON 也可用嚴格 JSON 格式安全編輯，PostgreSQL／SQL Server xml 與 SQLite XML 則會拒絕 DTD／外部實體，兩者皆限制為 1 MiB 字元；PostgreSQL inet／cidr／macaddr／macaddr8 會驗證 prefix、host bits 與 MAC 長度並保留 subnet prefix，time with time zone 使用含微秒與明確 offset 的純時間格式，interval 則以 `months=<整數>;days=<整數>;microseconds=<整數>` 保留三個原生分量，並依 `interval(p)`／`YEAR TO MONTH`／`DAY TO SECOND(p)` 等宣告拒絕會被 server 丟棄或取整的值，pg_lsn 以斜線分隔的兩組 32-bit 十六進位值編輯，oid／xid／cid／xid8 以完整無號範圍的十進位值編輯，tsvector／tsquery、6 種內建 range／6 種 multirange、一般／多維／自訂 element type array、point／line／lseg／box／path／polygon／circle，以及 jsonpath、pg_snapshot／txid_snapshot、hstore、ltree／lquery／ltxtquery、reg* 物件參照和其餘 enum／composite／extension 自訂型別，都由 PostgreSQL 權威 parser 保留原生語意；沒有原生等號的型別會以載入時的 canonical text 做 optimistic concurrency 比對。SQL Server text／ntext 也使用適合 legacy LOB 的參數與原值比對。超過 1 MiB 的既有 binary／JSON／XML／bit string／PostgreSQL 文字序列化型別／exact decimal 值只顯示摘要並維持唯讀。沒有 Primary Key 的 Table 仍可新增與瀏覽第一頁，但為避免不穩定定位，翻頁、修改及刪除會停用；generated 與尚未支援的進階型別維持唯讀。CSV / TSV 使用帶 BOM 的 UTF-8、固定 CRLF、完整引號 escaping 與試算表公式注入防護；JSON 使用無 BOM UTF-8，保留 NULL、數字、日期與 binary hex。匯出會先寫同目錄暫存檔，完整成功後才替換目標檔。一般查詢結果最多載入 10,000 列，避免誤查大表拖垮桌面程式；截斷結果匯出前會再次提醒只包含已載入資料。`connections.json` 永遠不保存密碼；使用者可在連線設定勾選 Linux Secret Service 或 macOS Keychain，寫入後會立即讀回驗證，不可用或失敗時安全退回本次執行期間的記憶體保存。更新檢查只接受受信任的 mySQLPunk GitHub HTTPS URL，並要求目標平台資產與同名 `.sha256` 都存在才開放下載；下載採大小限制、串流 SHA-256、同目錄暫存與驗證後原子替換，失敗不覆蓋既有檔案。Linux 套用前會在私有暫存目錄重驗 SHA-256，拒絕路徑穿越、link、過多檔案或超大展開內容；交易式安裝後新版若無法維持啟動，會回復舊入口並重新啟動舊版。

macOS 套用更新前會在目標 app bundle 同檔案系統的私有目錄重驗 SHA-256，拒絕路徑穿越、symlink、過多檔案或超大展開內容，並核對 plist 版本、RID、Mach-O 架構與 codesign。原子換包後新版若未通過五秒啟動檢查，會回復舊 `.app`、重新啟動並在下次開啟顯示原因與 log。

MySQL／MariaDB 字串原值以 byte-exact 方式比對，避免大小寫、重音或尾端空白不敏感 collation 讓過期編輯穿過 optimistic concurrency 防護。固定長度 `CHAR` 會先移除 server 本身無法 round-trip 的 U+0020 padding，保留合法短值編輯能力。

MySQL／MariaDB `ENUM` 會從 metadata 解析完整宣告成員，送出 SQL 前以 ordinal 精確比對。大小寫、重音、尾端空白或數字索引等會被 collation 靜默改成另一個成員的輸入會先被拒絕；逗號、引號、反斜線、空字串與數字字串等實際宣告成員仍可無損保存。

MySQL／MariaDB `SET` 同樣逐一精確驗證逗號分隔成員，防止大小寫、重音、尾端空白或數字 bitmap 偷換所選集合。輸入順序與重複成員依 SET 的無序集合語意正規化成欄位宣告順序；無法以逗號文字無歧義表示的歷史 schema 成員維持唯讀。

PostgreSQL `citext` 會先轉為 `text` 載入，避免 Npgsql 無法以 `object` 讀取 extension type；一般 String 原值再統一轉成 UTF-8 bytes 比對，避免 `citext` 或 nondeterministic ICU collation 把不同內容判為相等。固定長度 `character(n)` 仍沿用 PostgreSQL 去 padding 後的 canonical 文字。

PostgreSQL `json` 會保留原始空白與 key 順序，optimistic concurrency 因此也以 UTF-8 bytes 比對；其它連線即使只把 JSON 改成語意相同但文字不同的表示，也會觸發衝突而不放行 stale update。`jsonb` 仍使用原生結構等號，符合其 canonical storage 語意。

PostgreSQL array 的 optimistic concurrency 統一比對載入時 canonical text 的 UTF-8 bytes，因此 `json[]`、`xml[]` 等元素型別沒有原生等號運算子的陣列也能安全修改；其它連線變更陣列後，過期編輯仍會被攔截。

PostgreSQL `money` 會依資料庫目前的 `lc_monetary` 推導固定小數位，以不含幣別符號或千分位的 canonical 十進位文字無損編輯，並拒絕多餘小數與 8-byte 範圍溢位，避免 server 無聲取整或 Linux／macOS locale 改變顯示內容。

SQLite 的 `NUMERIC`／`DECIMAL` affinity 欄位會以 SQLite 實際儲存的 canonical 數值顯示；signed 64-bit 整數可完整編輯，其餘數值限制為 SQLite TEXT↔REAL 可穩定保留的 15 位有效數字。超出安全精度、千分位或無效格式會在送出前拒絕，避免 Microsoft.Data.Sqlite 的 decimal TEXT 參數再被 NUMERIC affinity 悄悄轉成失真的 REAL。

SQLite `DATE`／`TIME`／`DATETIME`／`TIMESTAMP`／`DATETIMEOFFSET` 以嚴格 ISO 文字驗證後使用 TEXT 參數原樣保存：日期不附加午夜、純時間不注入當天日期，一般日期時間拒絕 offset，`DATETIMEOFFSET` 則要求並逐字保留明確的 `±HH:mm`，所有日期時間都可保留最多 7 位小數秒。既有非 canonical 或 numeric temporal 值未修改時不會被其他欄位的修改連帶重寫。

SQLite `UUID`／`GUID` 宣告欄位使用 8-4-4-4-12 標準格式驗證，再以 TEXT 參數逐字保存大小寫；省略連字號、大括號或畸形值會在送出前拒絕。既有非標準 GUID 文字或 16-byte BLOB 未修改時仍以原 storage class 參與 optimistic concurrency，不會阻擋其它欄位的安全修改。

SQLite 文字、JSON、XML、temporal 與 GUID 欄位的 optimistic concurrency 會把資料庫原值與參數轉成 BLOB 後逐 byte 比對，不受欄位 `NOCASE`、`RTRIM` 或自訂 collation 的寬鬆等號影響；其它連線只改大小寫或尾端空白也會觸發衝突，不會提交 stale 修改或刪除。

MySQL／MariaDB `FLOAT`、PostgreSQL `real`、SQL Server `real`／`float(1–24)` 使用 4-byte IEEE 754 編輯器，其餘 `DOUBLE`／`double precision`／`float(25–53)` 與 SQLite `REAL` 使用 8-byte 編輯器。輸入必須能以目標 single／double 的 canonical 十進位文字 round-trip；會改變數字、溢位、下溢為 subnormal／zero、NaN 與 Infinity 都會在送出前拒絕。MySQL／MariaDB `FLOAT(M,D)` 另外檢查 scale、整數位與 `UNSIGNED`，而 `FLOAT` 載入會先提升為 `DOUBLE`，避開伺服器 text protocol 只輸出約 6 位有效數字造成的二次失真。

MySQL／MariaDB `TINYINT`／`SMALLINT`／`MEDIUMINT`／`INT`／`BIGINT` 會依 signed／unsigned／`ZEROFILL` 宣告驗證完整 1–8 byte 範圍；PostgreSQL `smallint`／`integer`／`bigint`、SQL Server `tinyint`／`smallint`／`int`／`bigint` 也使用各自的 metadata 邊界，domain 與 alias type 沿用 base type。越界值會在送出前拒絕，避免 MySQL／MariaDB non-strict 模式只回 warning 並把數值靜默截到最近邊界；`BIGINT UNSIGNED` 的 `18446744073709551615` 上界仍可完整 round-trip。

MySQL／MariaDB 的 Table 新增、修改與刪除會在單列交易 commit 前讀取同一連線的 server diagnostics；只要 mutation 回傳 warning，就會完整 rollback 並顯示 code／原因。這項 fail-closed 防護涵蓋 non-strict 模式下的 `CHAR`／`VARCHAR`／binary 截斷、TEXT／BLOB byte 上限與字元集替換，也能攔住 trigger 或未來進階型別回報的其他警告；沒有 warning 的合法邊界值不受影響。

MySQL／MariaDB `CHAR(n)` 會在讀回時移除尾端 U+0020 空白，且超出欄寬的純空白即使在 strict mode 也可能以 0 warnings 靜默截掉；PostgreSQL `character(n)` 也會補滿欄寬，並在一般字串語意中移除 padding，未指定長度的原生 `bpchar` 同樣是 blank-trimmed。跨平台編輯器會依 provider metadata 精準標記這些欄位，拒絕無法 round-trip 的尾端 U+0020 空白並建議改用 `VARCHAR`／`TEXT`；tab、NBSP、一般可變長度字串，以及 SQL Server 已有的 collation／byte 無損檢查不受影響。PostgreSQL 固定長度／blank-trimmed 字串的載入與 optimistic concurrency 也會統一採用去 padding 的 canonical 文字，避免未修改的資料列被誤判為衝突。

PostgreSQL `varchar(n)`／`character varying(n)` 會保留欄寬內的尾端空白，但規格允許在超出 n 的字元全是空白時無錯誤截斷。跨平台編輯器會從欄位與 domain metadata 保留字元上限，以 Unicode scalar 計數（emoji 不會被誤算成兩個 UTF-16 code units），並在送出 SQL 前拒絕所有超長輸入與無效 surrogate；未指定長度的 `varchar`、`text` 與 `citext` 不套用上限。`varchar(n)` 的 optimistic concurrency 仍保留尾端空白的差異。

MySQL／MariaDB 與 SQL Server 的固定長度 `BINARY(n)` 會從 metadata 保留精確 byte 數，輸入必須剛好是 n bytes；短值與長值都會在產生 SQL 前拒絕，避免資料庫在沒有 warning 的情況下自動補 `0x00` 或截斷。SQL Server alias type 會沿用 base `binary(n)` 限制；`VARBINARY`、BLOB、PostgreSQL bytea 與 SQLite BLOB 仍維持可變長。

SQL Server 一般 `char`／`varchar`／`text` 會先以 Unicode 參數保留原始輸入，再依欄位實際 collation 轉成 ANSI／UTF-8，核對 byte 上限及轉回 Unicode 後的完整內容；不可由 legacy code page 表示、或超出 multibyte byte 容量的文字會讓整筆交易回復，不再靜默變成 `?` 或被截斷。`nchar`／`nvarchar` 也依 metadata 的 UTF-16 byte 容量拒絕溢位；alias type 沿用 base type 限制。字串 optimistic concurrency 改比對實際 bytes，因此只改變尾端空白也會被視為外部修改。

SQL Server `money`／`smallmoney` 會固定顯示 4 位小數，並以各自的原生參數與正負範圍安全編輯；需要取整的第 5 位小數、幣別符號、千分位、科學記號與溢位會在送出前拒絕，不交給 server 無聲四捨五入。

MySQL／MariaDB 的 GEOMETRY、POINT、LINESTRING、POLYGON、MULTIPOINT、MULTILINESTRING、MULTIPOLYGON、GEOMETRYCOLLECTION，以及 SQL Server 的 geometry／geography，可用 `SRID=<非負整數>;<WKT>` 安全編輯；載入與寫回都保留 SRID，原值比對同時檢查 SRID 與 canonical WKT。畸形 WKT 會讓整筆交易回復；MariaDB 即使只回傳 warning／NULL，也會轉成明確錯誤，避免 nullable 欄位被靜默清空。超過 1 MiB 的 spatial 文字維持唯讀。

MariaDB 原生 `UUID`、`INET4` 與 `INET6` 欄位也可直接編輯。UUID 會正規化為小寫標準格式；INET4 僅接受 IPv4 並以原生 4-byte binary 保存，INET6 接受 IPv4／IPv6，且把 IPv4 依 MariaDB 原生語意保存為 IPv4-mapped IPv6。三者都先在本機拒絕無效格式，再以參數化原生 CAST 寫入，修改與刪除會用 canonical 值完成 optimistic concurrency 比對。

MySQL／MariaDB `DATE`、`DATETIME(p)` 與 `TIMESTAMP(p)` 會依欄位宣告保留 0–6 位小數秒，使用 canonical ISO 格式與對應的原生參數。介面會在送出前拒絕 DATE／DATETIME 範圍外、帶 offset 或超過欄位 fsp 的輸入，避免資料庫預設在沒有 warning 的情況下無聲四捨五入。

SQL Server `date`、`datetime`、`smalldatetime`、`datetime2(p)`、`datetimeoffset(p)` 與 `time(p)` 會使用各自精確的原生參數型別與 Scale。介面會在送出前拒絕超出原生範圍或必須取整的輸入，例如 `datetime2(3)` 的第 4 位小數、帶秒數的 `smalldatetime`，以及無法由舊式 `datetime` 精確保存的值；`datetime2` 可保留 0001 年與 100ns 精度，不會被通用 `DateTime` 參數降級成 1753 年起算的 `datetime`。

SQL Server `hierarchyid` 可用 `/1/2.5/` 形式的 canonical path 編輯；寫入使用原生 `hierarchyid::Parse()`，畸形 path 會讓整筆交易回復，修改／刪除則以原生 hierarchyid 等號完成 optimistic concurrency 比對。NULL 會保持 NULL，超過 1 MiB 的既有文字維持唯讀。

SQL Server `sysname` 與 `CREATE TYPE ... FROM ...` alias type 會同時顯示宣告名稱與 base definition，例如 `[dbo].[precise_amount] (decimal(18,6))`；編輯時依 base type 選擇安全參數與 precision／scale 驗證，仍由 SQL Server 拒絕字串長度、整數範圍等超出 alias 定義的值，交易不會留下半筆資料。

PostgreSQL domain 也會同時顯示 schema domain 名稱與 base definition，例如 `"public"."precise_amount" (numeric(18,6))`；integer、文字、exact numeric、enum／自訂型別、網路位址等 domain 會沿用 base type 的安全編輯器與參數，再由 PostgreSQL 套用 domain constraint。constraint 拒絕時會回復整筆交易。

PostgreSQL `timestamp(p)`、`timestamptz(p)`、`time(p)` 與 `timetz(p)` 會依欄位宣告保留 0–6 位小數秒；無時區型別禁止混入 offset，timestamptz 必須明確指定 offset 或 `Z`，載入固定顯示為 UTC `Z`。超出 p 的小數秒會在送出前拒絕，`time` 則保留 PostgreSQL 支援的 `24:00:00` 上界。

### Windows 完整版

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

推送 `v*` tag 後，`.github/workflows/release.yml` 會先由 Linux x64、Linux ARM64、macOS Intel 與 macOS Apple Silicon 原生 runner 產生四種架構的 self-contained 壓縮檔與 `.sha256`；每個包都會在相同 OS／CPU 架構上完成安裝或安全套用、啟動健康檢查與 rollback，再暫存為 immutable workflow artifacts。Windows runner 接著還原 NuGet、用 MSBuild 編譯 Release、安裝固定版本且先驗證 SHA-256 的 Inno Setup、執行 `scripts/package-release.ps1`，下載並核對所有跨平台資產，九個預期檔案齊全後才會一次建立或更新 GitHub Release。也可在 GitHub Actions 手動執行 `Release` workflow 並輸入版本號。所有平台都會檢查 tag / 手動輸入版本是否和 `AssemblyFileVersion` 一致，避免程式內更新檢查一直判定同一版本可更新；`scripts/New-ReleaseNotes.ps1` 會從 `CHANGELOG.md` 的對應版本產生繁體中文 `🚀 新增功能`、`🛠️ 問題修正與優化`、`📦 下載與更新`、`🛡️ 完整性與驗證` 四段說明。若缺少對應版本、必要段落或任一平台資產，發佈會直接停止，不會建立內容不完整的 Release。

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
| Linux / macOS 跨平台預覽 | 第二階段進行中 | .NET 8 Core 與 Avalonia 桌面程式支援 MySQL / MariaDB、PostgreSQL、SQL Server、SQLite 的連線設定／測試、database 與 Table / View 瀏覽、SQL DDL / DML / 查詢、結果網格、取消操作、CSV / TSV / JSON 安全匯出，以及具 Primary Key／optimistic concurrency 防護的 Table 資料編輯；四種 provider 均有 CRUD 與衝突 Docker／SQLite round-trip，Linux x64／ARM64、macOS x64／Apple Silicon 可建置。密碼可選擇存入 Linux Secret Service 或 macOS Keychain，絕不寫入連線 JSON；其餘 provider 與 Windows 完整版進階工作台功能待遷移。 |
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
| Punky AI 助理 | 可用 | 預設開啟的右側聊天面板，可關閉並從「檢視」選單重開，也能和物件詳細資料暫時收合到右側圖示列；支援 OpenAI 相容 API、Ollama／LM Studio，以及 Codex／Claude Code／Gemini CLI。選項頁以卡片顯示 CLI 路徑、帳號標籤與登入方式，可直接切換目前服務，進階區只顯示該服務需要的欄位；只讀取非敏感帳號欄位，不顯示 token 或金鑰。查詢工具列可把選取／目前 SQL 帶入解釋、最佳化、格式化或錯誤修正草稿，也能選擇 MySQL／MariaDB、PostgreSQL、SQL Server、Oracle、SQLite 目標方言，或建立自己的提示動作並釘選到選單；自訂檔只保存名稱、提示與釘選狀態。內容會先供使用者確認，不會自動送出。所有會改寫 SQL 的動作都能逐行比較，勾選要採用的連續變更區段後再套回原選取範圍或全文；編輯器中途有變更就拒絕覆寫，套用後也不會自動執行。可附上目前資料庫結構當上下文；API 金鑰存 Windows 認證管理員。 |
| 資料字典 | 可用 | 資料庫節點右鍵產生整庫結構文件（欄位、索引、CREATE 語句、目錄），輸出 HTML，瀏覽器可另存 PDF。 |
| 唯讀 ER 圖 | 可用 | 五種 provider 共用 schema 快照，可顯示資料表、欄位、主鍵與外鍵關聯；支援縮放、適合視窗、中鍵平移、重新整理與完整圖面 PNG 匯出。 |
| 資料庫結構差異 | 可用 | 可從 Models 或資料庫右鍵選擇另一個已開啟的資料庫，比對資料表、欄位型別、空值、主鍵與外鍵；支援跨 provider、交換方向、重新比較及 HTML 報告。整個流程唯讀，不會執行 DDL。 |
| 查詢輔助 | 可用 | 釘選查詢結果快照對照比較、連線清單即時搜尋、F11 專注模式。功能對照與後續規劃見 `docs/ROADMAP.md`。 |

## 已知限制

- Linux / macOS 目前是跨平台預覽版，涵蓋 MySQL / MariaDB、PostgreSQL、SQL Server、SQLite 的連線、metadata、SQL 工作流程、CSV / TSV / JSON 結果匯出、Primary Key 穩定分頁，以及常用 scalar、provider-aware integer range、無損高精度 DECIMAL／NUMERIC、single／double 浮點 round-trip 防護、MySQL／MariaDB BIT／ENUM／SET／完整範圍 TIME／YEAR／DATE／DATETIME／TIMESTAMP／UUID／INET4／INET6、PostgreSQL scalar temporal／bit string／timetz／interval／pg_lsn／oid／xid／cid／xid8／tsvector／tsquery／range／multirange／array／geometric／jsonpath／snapshot／hstore／ltree／reg*／enum／composite／extension UDT／domain、1 MiB 內 binary、JSON、XML、PostgreSQL 網路位址，以及 SQL Server scalar temporal／legacy LOB／hierarchyid／alias type／sql_variant 欄位的 Table 資料編輯；Oracle、MongoDB、Redis、Snowflake、其餘進階型別、模型與 AI 等功能仍需從 Windows 完整版遷移。Linux 缺少 `secret-tool`／Secret Service、macOS Keychain 不可用，或使用者未勾選保存時，程式重開後仍需重新輸入密碼；Linux 與 macOS 都已可安全下載、關閉、套用、健康檢查與 rollback。macOS 正式 Developer ID 簽署與 Apple notarization 仍需發行憑證。
- Oracle 的部分 DDL 還是會被權限、語法或物件型態擋下來；預覽會附上權限診斷 SQL 跟修復建議，但終究要看帳號實際有什麼權限。
- Windows 完整版對沒有 Primary Key 的資料表仍可用原始值組 WHERE 條件；欄位有浮點數或大文字時可能比不準，可在選項中改成唯讀。Linux / macOS 預覽版會直接停用無 Primary Key Table 的修改與刪除，只保留新增與瀏覽。
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

- `mySQLPunk.CrossPlatform.sln`: Linux / macOS 預覽版的獨立 .NET 8 solution，不會改動 Windows 完整版的建置與發版。
- `mySQLPunk.Core/`: 跨平台連線設定、安全保存、MySQL / PostgreSQL / SQL Server / SQLite provider、metadata、參數化資料列寫入、競爭衝突防護、SQL 執行、結果匯出與安全更新下載／套用核心。
- `mySQLPunk.Desktop/`: Avalonia 跨平台桌面 UI，包含連線、物件樹、SQL 編輯器、結果網格、Table 資料編輯與匯出操作。
- `mySQLPunk.CrossPlatform.SmokeTests/`: 跨平台 Core smoke tests，涵蓋密碼不落地、安全匯出、更新資產配對、Linux updater 參數／lock／結果解析、SQLite 端到端操作及四種 provider 的安全 CRUD／衝突情境。
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
- `scripts/`: Windows 打包（`package-release.ps1`）、Linux／macOS self-contained 打包（`package-cross-platform.sh`）與發版說明（`New-ReleaseNotes.ps1`）腳本。
- `packaging/`: Linux 使用者層級安裝／解除安裝腳本與 macOS app bundle metadata 範本。
- `docs/FEATURE_NOTES.md`: 功能完成紀錄。
- `docs/AUTOMATION.md`: 自動執行作業的操作、安全設計、檔案位置與命令列說明。
- `docs/CONNECTION_SECURITY.md`: 各 provider 的 SSL/TLS、SSH Tunnel、憑證與祕密保存方式。

## 協作規範

開發流程、測試、commit、發版、文件維護的規範都在 [`CONTRIBUTING.md`](CONTRIBUTING.md)，動手前先看一下。幾個最容易踩到的：

- 動手前先 `git pull --rebase origin master`；建置失敗不 commit、不 push。
- 行為有變就要跑 `tests/Run-SmokeTests.ps1`，全過才能 commit。
- 改了什麼就把 `CHANGELOG.md` 的 `[Unreleased]` 段補上（🚀／🛠️ 那兩個標題是發版腳本硬性檢查的，不能改）。
- 修掉 README「已知限制」裡的項目時，記得把那條拿掉，細節補到 `docs/FEATURE_NOTES.md`。
