# Changelog

## [Unreleased]

### 🚀 新增功能

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
