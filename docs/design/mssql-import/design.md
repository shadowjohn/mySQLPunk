# MSSQL SQL 檔匯入精靈設計

日期：2026-10-13
基底：master，d45c903（已合併 MSSQL 匯出修正）
狀態：使用者於 2026-10-13 回覆「好，進實作」核准本規格；[書面實作計畫](implementation-plan.md) 已整理並依明確進實作授權開始分工開發；實際 DB 匯入仍待驗收階段。

## 1. 需求與本次範圍

把 MSSQL 的「執行 SQL 檔／匯入」升級成專用精靈，支援 mySQLPunk 匯出的數 GiB SQL 檔及一般 T-SQL／GO。沿用匯出精靈的 Punky、自動循環動畫、一次聲音與靜音、進度、預估剩餘時間及預計完成時間。

使用者可勾選「遇到 SQL 錯誤時，略過失敗批次並繼續」，預設不勾。所有錯誤都寫入本機 error log；畫面提供唯讀 textarea，數量太多時顯示最近一段並清楚標示完整 log 路徑。

這次只改 MSSQL 的檔案執行入口，保留 MySQL／MariaDB／SQLite 及一般查詢編輯器的既有行為。支援順序執行，不做平行匯入、斷點續傳、自動重試、SQLCMD 指令、原生 .bak 還原或自動清空資料庫。

### 現有問題與方案選擇

舊入口會把檔案整份讀成 byte[]／string，再依分號拆解並使用共用 provider；不支援 GO、獨立取消或可信進度，傳入的 databaseName 也沒有在執行前明確切換。新流程使用獨立擁有的連線與串流 GO reader，不把大檔交給舊 splitter。

| 方案 | 取捨 |
| --- | --- |
| 程式內串流 GO reader＋專用 SqlClient session（採用） | 能取得每個批次的實際回應、錯誤、取消與進度；須完整驗證 lexer 及 bounded memory |
| 呼叫外部 sqlcmd | 可作獨立驗證工具，但需處理版本、外部程序及每批回應；不作精靈主要執行器 |
| 擴大舊整檔讀取流程 | 記憶體隨檔案增加，也無法保持正確 GO／LOB session 行為，不採用 |

## 2. 已核准畫面與保存位置

互動原型：[index.html](index.html)。原型的 SQL 執行、error log 與數值均為模擬，不連接 DB，也不寫入實際匯入紀錄。

截圖存入本設計的 `snapshot/`，保留原始瀏覽器截圖，沒有重新縮圖：

| 畫面 | 圖片 |
| --- | --- |
| 淺色，匯入進度、ETA 與 Punky | [progress-light.jpg](snapshot/progress-light.jpg) |
| 深色，帶錯誤完成與大量 error log | [error-log-dark.jpg](snapshot/error-log-dark.jpg) |

四步流程如下：

1. **檔案與目標**：選 SQL 檔，顯示大小、編碼、來源摘要及實際解析的 server／instance／database。使用既有連線設定；本次本機試跑使用 Windows 整合登入。
2. **匯入檢查**：完整串流預掃描，檢查 GO／編碼／批次上限、檔案完整標記、目標狀態、基本權限與 log 可寫性。顯示掃描進度，失敗時指出批次／行號並停用下一步。
3. **確認執行**：再次顯示檔案、實際目標、錯誤政策、log 路徑及部分寫入可能保留的說明。略過錯誤預設關；使用者確認目標後才可開始。
4. **執行與結果**：依序送出批次，顯示已處理檔案範圍、成功／失敗批次、目前行號、已用時間、ETA 與最近錯誤。結束後保留結果與 log 開啟／資料夾／複製路徑功能。

Punky 預設自動循環播放，不設播放或暫停按鈕；聲音僅首輪一次，可靜音。執行、停止中、完成、帶錯誤及失敗使用既有素材與狀態文案。過程中取消按鈕保持可到達；視窗縮放、高 DPI、深淺色與鍵盤操作均須驗證。

## 3. 檔案預掃描與 GO 規則

檔案以唯讀 handle 開啟，採 `FileShare.Read`，在預掃描、確認與執行期間持續持有；離開精靈或更換檔案才釋放。無法取得穩定讀取 handle 時不開始。預掃描只讀檔與 DB metadata，不執行檔內 SQL。

採兩次順序讀取：先完整掃描取得總 bytes／批次數／最大批次／SHA-256／格式資訊，再 rewind 同一個 handle 執行。兩次都用固定 buffer，不保存所有 batch 或全部行號索引。更換檔案、目標或關鍵設定會使預檢失效；開始前再次讀取目標狀態。

- v1 支援嚴格 UTF-8（有／無 BOM）及有 BOM 的 UTF-16 LE／BE；拒絕無法判定或有無效序列的編碼，不能以替代字元默默執行。來源檔案不改寫。
- GO 不分大小寫，須位於字串、quoted identifier、bracket identifier 與註解之外的獨立一行。支援前後空白及 `GO -- 註解`；GO 不送給 SQL Server。v1 不支援 GO 重複次數與同一分隔行的 block comment，預掃描發現這類 GO 語法時明確拒絕。
- lexer 跨 buffer 維持狀態，處理單引號／N 字串與 `''`、雙引號與 `""`、方括號與 `]]`、單行註解、巢狀 block comment、CRLF／LF 及多位元字元。字串／註解中的 GO 保留原文。行本身也不得以無上限 ReadLine 累積。
- EOF 的最後一個非空批次照常執行；未閉合的字串、identifier 或 block comment 在預掃描擋下。空批次不送出，不用分號拆解任意 T-SQL。
- `:r`、`:setvar`、`:connect`、`!!` 等 SQLCMD 指令在有效指令位置出現時拒絕；不展開 `$(name)`，字串中的相同文字維持原值。

**單一 GO 批次上限為解碼後 32 MiB，即 16,777,216 個 UTF-16 code unit。** 超過時於預掃描、任何 DB 寫入前停止，列出批次及行號。不能插入 GO 或用分號硬切來避開限制。這是執行批次的記憶體界限，不是欄位大小限制：數十 MiB 甚至更大的文字／binary／Geometry 仍可透過既有分段腳本跨批次匯入。

既有 exporter 的資料批次最多約 4 MiB UTF-8，LOB chunk 更小；schema／view 定義沒有同樣的通用上限，仍須實測預掃描。32 MiB 也不是整個程序的總記憶體承諾，StringBuilder、最終 string 及 SqlClient 可能同時持有多份；驗收要量測峰值及長時間記憶體是否隨檔案成長。

辨識目前 mySQLPunk 完整匯出檔時讀取 footer 的 job／tables／rows 及來源讀取模式；缺少或格式不完整的 native 完成標記不視為可完整還原的匯出檔。一般 T-SQL 不要求該 footer，但顯示「無匯出筆數摘要」。識別規則以既有 exporter 的結構及受控 marker 為準；註解與 footer 都不是簽章或安全認證。執行時再計算串流 digest 與預掃描比對，差異會令結果失敗，不能藉此回復已執行的 SQL。

## 4. 專用連線、目標與交易

每個作業經既有 `ConnectionOpenService`／owned session 開啟專用連線，以選定的 Initial Catalog 明確指定目標。複製必要的連線設定，不借用資料庫樹或查詢頁面的共用 provider；SSH owner 也隨本次 session 釋放。所有 GO 批次都使用這個 session，不自動重連或換線。

開始時記錄實際 server／instance、版本、DB_NAME、database_id、可寫狀態及目前登入可見的 metadata。確認頁與結果頁顯示解析後的實際目標；讀取失敗、目標消失／唯讀、身份不符或無法確認必要條件時不開始。沒有 sysadmin／ALTER DATABASE 並非一律拒絕的理由；依腳本範圍檢查基本建立／寫入能力，實際 SQL 權限錯誤仍需完整記錄。

辨識為包含建立結構的 mySQLPunk 匯出檔時，目標須無使用者資料表／檢視／程序等物件，且需有足夠 metadata 可見性來確認；資料庫既有 schema 本身不代表非空。資料型匯出與一般 T-SQL 不一律要求空庫。權限或語法預檢不是任意 SQL 能成功的證明，也不能靠試跑 SQL 檢查權限。

精靈不會自動建立、清空、覆寫資料庫，不切 recovery model、不開關 SNAPSHOT、不發 GRANT 或 KILL，也不會放寬外鍵／檢查約束。使用者選擇執行的 SQL 本身仍可變更資料；一般腳本依 SQL Server 原有權限執行，本功能不是 SQL sandbox。

v1 不接受獨立 T-SQL `USE` 切換至其他資料庫；lexer 能辨識這類直接語法並在預掃描擋下。三段名稱、動態 SQL 或 stored procedure 仍可能存取其他 DB，不能宣稱 target pinning 會攔住所有跨庫存取；執行前文案須要求使用者確認腳本來源與目標。每批完成再確認目前 database_id，發現改變就停止。

保留批次原文、順序及 session 的 SET、IDENTITY_INSERT、temp table、LOB staging 狀態。不中途插入全檔外層 transaction，也不改寫腳本自己的 transaction；允許一般腳本的交易跨 GO，但這種情況遇錯不能套用自動略過。

批次間為確認交易與目標而插入的狀態查詢可能改變 @@ROWCOUNT／@@ERROR；v1 不保證這兩個 statement intrinsics 跨 GO 保持原值，一般腳本預檢與文件需明示。需要保留時應在同一 batch 先自行存入變數／temp table；既有 exporter 腳本不依賴這兩值跨 GO。此相容邊界於實作計畫與覆核裁決中固定，並以測試驗證。

成功回應只代表這個 batch 已完成執行，不代表其中的交易已提交；失敗／取消批次也可能已部分寫入。先前已提交的變更不會因取消整份還原。自然 EOF 尚有未結交易時不能顯示成功；停止後嘗試回復本次專用 session 的未提交交易並釋放連線，回復失敗或結果不明須記錄。

## 5. 順序執行、大欄位與錯誤政策

worker 一次最多執行一個 SqlCommand，下一批在前一批結果已處理後才送出。CommandTimeout 預設 0（允許長指令），取消機制獨立可用；若未來加入有限 timeout，timeout 一律停止，不自動重試。產生結果集的一般 T-SQL 也要逐 result／逐 row 讀完並丟棄，不建 DataTable 或將整個結果載入畫面。

mySQLPunk LOB／Geometry 使用同一 job 的 `#mp_<guid>_*` temp tables，維持原本順序／長度／Consumed／Failed 檢查，Geometry 依原腳本 Deserialize。不能清除失敗旗標、跳過 chunk 或重播已消耗 row 來假裝完成。

| 情況 | 行為 |
| --- | --- |
| 預設未勾略過，收到 SQL 錯誤 | 記完整 log，停止排程，結果為失敗 |
| 已勾略過，一般 SQL 錯誤符合下述條件 | 記完整 log，略過失敗的 GO batch，再送下一批；最後為「執行完成，但有錯誤」 |
| timeout、取消、斷線、嚴重錯誤、未知執行結果 | 停止；不假裝是可略過的普通錯誤 |
| parser／編碼／檔案 IO／log IO 錯誤 | 停止；不得在沒有可保存錯誤紀錄的狀態繼續 |
| 未結／不可提交／無法查明的交易，或 LOB 協定失敗 | 即使勾略過仍停止，保留可追查狀態 |

一般 SQL 錯誤只有在以下條件全部成立時才可略過：

1. 使用者已勾選；原連線仍開啟，未收到取消；錯誤屬一般 SQL 錯誤（class 11–16），不是 timeout／attention／傳輸錯誤或 native 協定錯誤。
2. 本批執行前與錯誤後均能確認 `@@TRANCOUNT=0`、`XACT_STATE()=0`，且目前仍為選定 database_id；狀態查詢使用獨立 5 秒 timeout，失敗就停止。
3. 該 batch 不涉及 native LOB 初始化／組裝／提交，且已辨識的 job 狀態沒有 Failed、未消耗 row 或不符預期的狀態；無法確認狀態就停止。
4. 本批所有 SQL error detail 均已成功寫入並 flush 至 log。

以 provider 錯誤碼、class、交易狀態及受控協定識別判定，不以英文／中文 error message 字串決定恢復。native 批次識別與 temp identifier 必須經 lexer 及 GUID 格式驗證；不能把任意 footer 內容接成 SQL。初始化尚未發生與預期 state table 卻遺失須分開。

特別注意：LOB CATCH 會先 rollback 短交易，再把 temp state 的 Failed 設為 1。因此交易已回到 0 仍不表示可繼續，包含最後 INSERT 的轉型／約束失敗也要停止。無法安全辨識的 native 狀態採停止，不能默默降為一般略過。

「略過」是失敗 batch，不是單一 row；同批已生效的 SQL 不會自動撤回。成功數、失敗／略過數及當前結果不確定的批次分開，不能由 ExecuteNonQuery 的 affected rows 推算實際匯入筆數，因為 LOB staging 更新也會計入。

## 6. Error log 與 UI 記憶體界限

每次作業建立獨立 UTF-8 log，預設 `%LOCALAPPDATA%\mySQLPunk\ImportLogs\import-<timestamp>-<job>.log`。開始 DB 寫入前必須已成功建立並寫入作業表頭；檔名唯一且不覆寫既有 log。檔案路徑在確認、執行與結果頁可見。

表頭包含 job、來源路徑／大小／digest／編碼、實際 server／database、錯誤政策與開始時間。每個 SQL 錯誤保存時間、batch Int64 序號、來源行範圍與原始 byte 範圍、provider error number／state／class／相對與換算行號／message、採取的動作及狀態檢查結果。多個 SqlError 都要記錄，不只第一個。完整 log 指所有錯誤都有紀錄，不額外附整段 SQL、LOB payload、連線字串或密碼；SQL Server 原始錯誤訊息本身可能含資料值，紀錄維持本機使用者目錄。

error log 串流追加，每次要略過前 flush；錯誤不能累積到最後才存。job 結尾寫入結果、成功／失敗／未確認批次統計、最後確認位置、取消及 cleanup 結果。log 建立／追加／flush 失敗就停止，畫面保留 bounded 最新錯誤及存檔失敗原因，不宣稱完整 log 已保存。

textarea 唯讀，以 ring buffer 保留最近 100 筆或 64 KiB UTF-16 文字（先到者為準），單筆過長只截 UI。總錯誤數另以 Int64 累計；不要在記憶體保存所有錯誤／失敗批次或每批 log string。截段文案顯示「僅顯示最近 … 筆；共 … 筆，完整紀錄：<path>」。開啟完整 log／資料夾／複製路徑直接作用於實際已建立的檔案；成功、帶錯誤、失敗與取消都保留紀錄。

## 7. 進度、ETA 與結果核對

預掃描與執行分開計算進度。執行進度以原始 bytes 為基礎，只在 command 已取得結果並完成處理後推進；不能用 read-ahead 的 FileStream.Position 當已匯入進度。成功覆蓋 bytes 與失敗／略過覆蓋 bytes 分開，勾略過時主標籤為「已處理檔案範圍」，不可把 100% 說成全部成功。

UI 約每 200 ms 合併更新，worker 不對每個 row／chunk 送 UI invoke。目前 batch 序號、來源行範圍與已確認邊界使用 Int64。只有能由 native 格式可靠辨識的表名／階段才顯示，否則顯示目前批次；不猜測任意 T-SQL 的物件清單。

ETA 採最近約 10 秒已確認處理 bytes 的速率，至少 3 次樣本、2 秒觀察後才顯示「約 …／預計 …」。5 秒無新確認、長指令、索引／約束階段或波動太大時改顯示「計算中／目前指令執行中」，不倒數至負數、不把讀完檔案等同工作結束。建立索引、外鍵與結果核對完成前仍為執行中；預掃描、執行及結果核對各有狀態。

所有跑到 EOF 的作業（包含已略過錯誤者）都必須檢查 session／交易與檔案 digest。所有已辨識的 native 模式（含 DataOnly）若曾初始化 LOB，結尾須確認預期 state table 存在且只有一筆、Failed=0、Consumed=1、Sequence=ConsumedThrough；未初始化時不要求 state table。任何這類檢查失敗都進入 Failed／CleanupUncertain，不能以 CompletedWithErrors 蓋過未結交易、未消耗 LOB 或 digest 不符。

摘要筆數核對才限定無錯誤的 native 完整結構匯出：在原本空的目標列出實際 user table 數並逐表 COUNT_BIG，對照 footer 表數／總筆數；這個核對步驟可取消，且要單獨顯示進度。資料型匯出或一般 T-SQL 沒有足以進行同樣比較的 manifest，無錯誤結果只聲明「所有批次執行完成，未提供資料內容核對」；已略過者維持帶錯誤結果與明確未通過完整核對的說明。

footer 是預期摘要，COUNT_BIG 比對也不是逐欄值／來源 DB 雜湊證明；不連正式來源庫做比較。來源 READ COMMITTED 的跨表不一致警告仍保留；因此最後外鍵建立可能失敗，不能自動改成 NOCHECK 或刪資料。帶錯誤時不給「完整還原通過」標示。

| 結果 | 顯示與證據 |
| --- | --- |
| Completed | 所有批次完成、無未結交易、必要檢查通過；明示實際完成了哪些核對 |
| CompletedWithErrors | 已遍歷整份檔案，但有失敗／略過；琥珀色，完整 log 路徑與統計 |
| Failed | 停止原因、最後確認批次、當前失敗／不確定批次與 cleanup 結果 |
| Cancelled | 已停止排程、command 已結束、session 已釋放；明示先前寫入可能保留 |
| CleanupUncertain | 無法確認當前結果／釋放狀態時明示仍有不確定性，不冒稱安全取消或還原完成 |

## 8. 取消與生命週期

按取消後停止排程並顯示「正在停止匯入…」，向本次 SqlCommand 發出 Cancel；取消不是保證立即中斷。等待真正回應期間 UI 保持可回應，不能先把結果改成已取消，也不能開始另一輪匯入。

command 回應後，用本次 session 進行必要的未提交交易 cleanup（獨立 10 秒 timeout），釋放 command／reader／session／SSH owner／file handle 並完成 log。若 Cancel 10 秒內未得到回應，嘗試關閉本次擁有的連線；仍無法確認時進入 CleanupUncertain，保留工作識別與最後位置。不能 KILL 其他 session、在仍使用中的 worker 上二次執行，或以自動重連掩蓋狀態。

視窗關閉時若仍在跑，走同一取消流程並避免 callback 更新已 Dispose UI。沿用 export wizard 的 job generation／owned-resource 模式。取消可能等待伺服器 rollback；已提交部分不會自動撤銷。結束後「重新檢查匯入」回到預檢，不直接重跑失敗 batch，不自動刪除部分匯入物件。

## 9. 程式邊界

沿用 .NET Framework 4.7.2、WinForms、System.Data.SqlClient 及專案 MSBuild 工作流，不新增 WebView 或外部 sqlcmd 執行依賴。

- `Form1.MssqlImport.cs`：集中 MSSQL 檔案入口、明確目標與精靈啟動，既有其他引擎路由不變。
- `MssqlImportWizardForm`：四步狀態、Punky、確認／取消／結果／bounded log UI。
- `lib/MssqlImport`：分離 request／result／progress 模型、streaming lexer／preflight、owned-session sequential runner、error log 及 ETA estimator；核心流程可獨立測試。
- 共用已有的連線擁有權與 Punky 控制，不擴大 `IDatabase` 公開契約，也不把新 reader 塞進舊 MySQL splitter。

以上是責任邊界，具體工作順序與測試安排於規格核准後列入實作計畫。

## 10. 驗收條件與目前證據

### 實作後必須取得的證據

1. Lexer：每種字串／identifier／註解內 GO、巢狀 comment、跨 buffer／UTF-8／UTF-16／CRLF、超大單行、EOF、GO n／SQLCMD 拒絕；預掃描錯誤時 DB 寫入數為零。
2. 有界資源：相同批次形狀的 1 倍／10 倍大檔，無整檔 bytes／string／AST／batch list；含 32 MiB 邊界與大批次拒絕，峰值記憶體受批次界限控制且不隨檔案長度線性增加。
3. 同 session：temp table、SET、IDENTITY_INSERT、跨 GO transaction；64 MiB binary、18 MiB Unicode、Geometry 分段匯入後以來源測試資料的 bytes／hash／SRID／NULL 及 metadata 比對。
4. 錯誤：預設停止、勾選後一般錯誤繼續；交易內錯誤、XACT_ABORT、斷線、timeout、log IO、LOB chunk 遺失／重複／亂序、Geometry／XML 轉型及 final INSERT 約束失敗仍停止。當前部分寫入及未知結果不得被標為成功。
5. Log／進度：所有 SqlError 落檔、UI 截段／完整路徑、故障 flush、byte 與批次 Int64、read-ahead 不提前算成功、ETA 暖機／長指令／索引／核對階段、取消與關閉生命週期。
6. WinForms 與既有 smoke／Debug／Release build；測試執行檔依專案既有 smoke 入口，不重建先前被防毒隔離的 ReportExportTests.exe。
7. 最後才對使用者授權的本機 SQL Server 2019／Transfer 執行實際 4.17 GiB 檔案試跑，保留耗時、峰值記憶體、log、物件／筆數及約束核對結果；失敗部分保留，清除／重跑前先呈現具體狀態，不自動破壞使用者 DB。

### 本次已取得的證據

- 提供檔案為 `C:\Users\johnho\Desktop\Transfer_dump_20261013_165730.sql`，4,478,414,151 bytes，UTF-8 無 BOM／CRLF；僅 bounded 首尾檢查，footer 預期 126 tables／3,247,785 rows，來源 READ COMMITTED。尚未完整掃描／執行此檔。
- 本次研究時以 Windows 整合登入唯讀確認 `localhost` 解析為 `JOHN\MSSQL2019`，版本 15.0.2000.5，Transfer compatibility 150、ONLINE／READ_WRITE／FULL，當時 user tables／views／procedures 均為 0；具有 VIEW DEFINITION／CREATE TABLE／ALTER 基本權限。執行前須重新檢查，這不是未來狀態保證。
- 目標 data／log 當時各 8 MiB、64 MiB 固定成長且位於 C 槽；目前 C 槽約 152.7 GiB 可用，D 槽約 2 GiB。來源位於 C，log 預設也位於 C；SQL 文字檔大小不能直接當作所需 DB／transaction log 空間，實際匯入須觀察增長。
- 瀏覽器原型已驗證四步導覽、超大 batch 阻擋、預設不略過、勾選後模擬 SQL 547 繼續到帶錯誤完成、未勾時停止、1,283 筆只顯示最近 100、唯讀 textarea、log 示意操作、取消先停止中後取消、深淺色、640 px 無水平溢位與靜音切換。console warning／error 為 0。
- 尚未實作 native importer、實際 log 落檔／DB 寫入、完整大檔還原、native UI／實際音訊聽感驗收；上述畫面證據不能取代這些驗收。

## 11. 主要技術依據

- [SQL Server GO](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/sql-server-utilities-statements-go?view=sql-server-ver15)：GO 是 client batch delimiter，不是可直接送給 SqlCommand 的 T-SQL；本版支援範圍另按本規格明訂。
- [SqlCommand.Cancel](https://learn.microsoft.com/en-us/dotnet/api/system.data.sqlclient.sqlcommand.cancel?view=netframework-4.8.1)：取消是嘗試取消執行，不可把呼叫成功當作伺服器已停止。
- [TRY…CATCH](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/try-catch-transact-sql?view=sql-server-ver15)：client attention／broken connection 不保證由 T-SQL CATCH 處理，cleanup 不可只靠腳本 CATCH。
- 專案來源：`Form1.cs` 的 ImportSqlWithDialog／ImportSqlScript／SplitSqlScript；`MssqlLobScriptWriter` 的 Guarded／Consumed／Failed 協定；`MssqlSchemaScriptWriter`、`MssqlExportArtifact`、`ConnectionOpenService`、`MssqlOwnedSession` 與 export wizard／smoke tests。
