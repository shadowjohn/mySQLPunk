# MSSQL 大量資料 SQL 匯出精靈設計

日期：2026-10-12

狀態：互動畫面、修訂規格與[實作計畫](implementation-plan.md) 已獲使用者同意；2026-10-12 使用者選擇「同意計畫，分工實作並逐項覆核（建議）」。產品實作、必要的隔離還原／容量驗證及最終覆核已完成；實際證據見 [verification.md](verification.md)。最後新增提示已於 2026-10-12 完成深／淺色原生 fixture 畫面重驗；SSH、實體 DPI／聲音及原生長作業仍待驗證；尚未合併主線或推送。

2026-10-12 修訂：使用者要求調整單筆大欄位限制，關心 geometry／長文字，並接受極端型別後續再擴充。取消原草案「單列 SQL 超過 16 MiB 就拒絕」規則；一般長文字／binary 改為可分段輸出，原生 spatial 型別仍列後續擴充。型別可容納的大小是設計目標，不代表所有理論上限已完成實機驗收。

## 1. 已確認的目標

使用者要升級 MSSQL 的「建立備份」及「匯出結構與資料」，解決大量資料時畫面卡住、記憶體不足與無法掌握進度的問題。第一版限定本機 `.sql` 邏輯匯出，來源通常仍持續新增、修改資料。

2026-10-12 使用者看過互動原型後回覆：「畫面很棒，可以同意」。此確認記錄為畫面與互動方向的驗收，不代表真正串流匯出、DB 一致性或還原已驗證。

- 原型：[index.html](index.html)；操作：[README.md](README.md)。
- 淺色：[preview-progress-light.png](preview-progress-light.png)。
- 深色：[preview-progress-dark.png](preview-progress-dark.png)。
- 流程：選表 → 輸出設定 → 確認／前置檢查 → 執行／結果。
- 保留雙層進度、逐表狀態、實際筆數與檔案大小、耗時與 ETA、右側 Punky、自動動畫與靜音、固定底部取消按鈕。

## 2. 範圍與實作方向

### 本版納入

1. 既有 WinForms MSSQL 互動式本機 SQL 匯出入口共用專用精靈及 worker；依入口預選結構與資料、僅結構或僅資料。資料庫入口預設全選，單表入口只預選指定資料表。
2. 原生 SQL Server 型別與結構的正確腳本、逐表串流資料、大欄位分塊輸出、可取消查詢、串流檔案完整性驗證。
3. 持續寫入來源採同一個 SNAPSHOT 交易；不自動變更 DB 設定。
4. 「建立備份」保留目前選用的備份鏡像與保留份數功能；一般手動 SQL 匯出不因此新增鏡像。
5. 沿用現有 Punky 素材、ThemeManager、UiKit、獨立連線及 SSH transport 的生命週期。

### 本版不納入

SQL Server 原生 `.bak`、還原工具重寫、排程／自動備份改造、其他 provider 重構、暫停或斷點續傳、平行多表讀取、壓縮／加密輸出，以及伺服器登入、權限、SQL Agent job 等完整災難復原能力。

此功能是所選物件的 SQL 邏輯匯出，不以「建立備份」舊選單名稱暗示等同完整 `.bak`。精靈確認與結果必須列出實際包含／未包含範圍。

### 選定的架構

新增 MSSQL 專用匯出服務與表單。另一方案是把所有 provider 的通用 dump API 全面改成串流，但會擴大 MySQL、PostgreSQL、SQLite 等既有行為的回歸範圍，因此留待有明確需求再做。

不以現有 `BuildDatabaseDump()`／`BuildTableDump()` 的字串結果餵給背景執行緒；那仍會累積整份 SQL。也不把報表 wizard 繼承成通用 wizard framework。

## 3. 已查到的根因與相容性注意

- `Form1.cs` 的非 MySQL 本機 dump 和 MSSQL「建立備份」同步執行，會占用 UI 執行緒。
- `DatabaseDumpService.cs` 累積每表與整庫 StringBuilder，最後才寫檔；分頁查詢沒有解除輸出記憶體限制。
- `BackupIntegrityService.cs` 對 SQL 使用 `File.ReadAllText`，鏡像後也可能再次整檔讀入。
- `my_mssql.cs` 分頁使用 OFFSET；無 PK 時使用任意排序。新 worker 不沿用分頁路徑，每表只開一個連續 reader。
- 現有建表語法使用跨庫 copy 型別映射，會改變 varchar、datetimeoffset、uniqueidentifier 等語意，也缺少 identity／computed 等必要 metadata。
- `SecuredDatabase` 包裝 SSH lease。直接將 `IDatabase` 強制轉型成 `my_mssql` 在 SSH 路徑不可行。
- 目前發布流程有 delete-then-move 缺口；新路徑不得先刪除舊目標檔案。

## 4. 畫面與操作契約

### 選擇資料表

- 顯示 schema、表名、comment、估計筆數與資料大小；以 schema/name/object_id 識別，不以顯示文字識別。
- 可搜尋名稱及 comment。搜尋不清除其他資料表的選取；「全選全部」明確作用於全部資料表。
- 零選取停用下一步。metadata 載入在背景執行，取消／失敗有明確狀態。
- 資料大小是來源儲存量估計，不等同最後 SQL 大小；缺少估計資訊時顯示未知，不以 0 替代。

### 輸出設定

- 模式為結構與資料、僅結構、僅資料；使用 Windows 儲存檔案對話框選本機路徑，輸出 UTF-8 `.sql`。
- 若目標已存在，確認覆寫後仍保留原檔直到新檔驗證與安全替換完成。
- 庫級結構匯出須保留既有檢視定義能力，提供「包含檢視定義」選項，庫級入口預設勾選；僅資料不包含檢視定義。這是原型未展示、正式版需補上的選項。
- 備份入口若有鏡像設定，確認頁顯示啟用狀態及目的地；不將登入密碼／連線字串放入畫面或日誌。

### 確認與前置檢查

列出來源、模式、表與檢視範圍、輸出路徑、估計工作量、一致性模式、鏡像，以及具體不支援項目。檢查失敗須指出物件及原因，保留選取供修正。

未啟用 SNAPSHOT 時，含資料模式停在此頁；不能自動降級成一般讀取或 NOLOCK。僅結構不需要跨表資料快照，但仍檢查 schema 變動與定義可讀性。

開始按鈕捕獲不可變的本次 request；執行期間不能改選取、路徑或模式。原型頂端的任意跳頁工具不進正式產品。

### 執行與結果

- 階段：準備 → 結構 → 逐表資料 → 檔案驗證 → 本機發布 → 選用鏡像 → 結果。
- 同時顯示整體估計進度、目前表進度／已讀列數、表名與 comment、已完成表數、SQL 已寫 bytes、elapsed、速率與 ETA。
- 詳細紀錄只留有上限的操作紀錄，不逐列寫 UI log，不記資料內容或秘密。
- 取消按鈕固定在底部。按取消後顯示「正在取消…」，等 worker 清理結束才顯示已取消。
- 結果區分成功、取消、失敗，以及本機成功但鏡像未完成；只有已發布產物提供開啟資料夾。
- SQL 可能非常大，完成後不自動把整個檔案載入應用程式編輯器。

### Punky

直接重用 `PunkyReportLoadingControl`。開始作業時 `BeginExport()` 自動 loop，原音只播放一次；靜音立即停聲、取消靜音不重播。只有靜音按鈕，無播放／暫停。完成、失敗、取消後 `EndExport()`；關閉／Dispose 釋放聲音、Timer 與素材資源。保持深淺色可讀性與 DPI／較小工作區的取消可達性。

## 5. Worker、連線與一致性

1. UI 只捕獲本次選擇及必要的連線設定，worker 經既有安全連線流程取得獨立連線及 SSH lease；不得借用主視窗 provider。
2. 在 `ConnectionOpenService` 邊界提供最小的內部 MSSQL 工作連線存取方式，保持 wrapper 及 raw SqlConnection 同一擁有者。不改動所有 provider 的 `IDatabase` public contract，不以另開連線字串繞過 transport 生命週期。
3. 前置檢查讀取目標 DB 的 `snapshot_isolation_state`，僅 ON 接受含資料快照；過渡狀態不當作可用。只開 `READ_COMMITTED_SNAPSHOT` 不等於這次跨表交易已使用 SNAPSHOT。
4. 使用者選擇期間不持有快照。按開始後重做必要檢查，在指定資料庫開始一筆明確的 SNAPSHOT transaction，必要來源查詢均使用同一交易。
5. 所有選取表依序用同一交易讀取；不能每表重新開交易，也不能中途重新連線續跑而仍宣稱同一時點。
6. SNAPSHOT 不保證 metadata 可任意變更。預檢後凍結本次物件定義；物件變更、不可讀、schema 變動導致查詢失敗等情況中止本次工作，不跳過失敗表後顯示成功。
7. 讀完最後來源資料及必要來源查核後立即結束交易並釋放 DB／SSH 資源；本機驗證、發布、mirror 不延長快照持有時間。
8. 清楚提示長時間快照需要保留資料版本；不承諾零負載或完全無阻塞。取得不到版本儲存監控權限不應成為一般匯出的額外權限門檻。

參考：[Microsoft Snapshot isolation](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/sql/snapshot-isolation-in-sql-server)。

## 6. 有界記憶體與 SQL 正確性

### 讀寫方式

- 每表以明確欄位清單開啟 `SqlDataReader`／`SequentialAccess`，避免 OFFSET、整表 DataTable 或整表字串。
- 不為取得精確進度先對每表完整 COUNT；使用 metadata 估計並保留未知狀態。
- 文字與 binary 大欄位使用固定大小 buffer 分段轉義／十六進位寫出；不能先完整 GetString/GetValue/byte[] 後才分塊。
- writer 可緩衝小段輸出，不累積整表、整份檔案或一個巨大 INSERT。一般小列走明確欄位 INSERT；整列的小資料緩衝預算最多 256 KiB，超過即轉分段路徑，不視為資料不能匯出。適當加 GO 分批，不把整個資料匯入綁成單一巨大 transaction。
- 資料大小與 SQL 指令大小分開管理。大欄位以至多 64 KiB 原始 binary 或 16K 個 UTF-16 碼單元為起始 chunk 目標；轉義、hex 膨脹與字元邊界均算入實際大小。一般資料 statement 以 256 KiB 為生成預算，GO batch 目標不超過 4 MiB，必要時進一步縮小 chunk。這些是可調整的內部緩衝／批次參數，不是單筆資料的拒絕上限；不得在 INSERT／UPDATE 中間插 GO，亦不截斷欄位值。
- bytes、筆數、時間計量使用可容納大檔案的型別；顯示及 I/O 不截成 32-bit int。
- 查詢等待可取消，避免沿用一般查詢的短 timeout。worker 的長查詢 command 必須直接配置 timeout，不能依賴目前不涵蓋 local command 的 `setTimeout()`。

參考：[Microsoft SqlClient streaming](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/sqlclient-streaming-support)。CancellationToken 搭配所屬 SqlCommand.Cancel，不能只期待 token 自動中斷正在等待的 reader。

SQL Server 的 batch 長度受 network packet size 影響，總檔案可達數 GiB 不代表單一 statement 可以任意大。本版限制生成批次的大小，讓受支援的大欄位跨多個批次重建；不再以 16 MiB 定義通用單列上限。驗收使用支援 GO 的 Microsoft `sqlcmd`，記錄工具／伺服器版本與輸入設定。既有 mySQLPunk 匯入端仍需獨立驗證，不能假設它支援無界大檔。[Microsoft 容量限制](https://learn.microsoft.com/en-us/sql/sql-server/maximum-capacity-specifications-for-sql-server?view=sql-server-ver17)

### 大欄位分段還原

1. 大列在同一個匯入 session 的私有 `#temp` staging 區組裝；不依賴目標表有 PK，不直接先插入空附件再反覆 UPDATE 目標列。先完整組裝及驗證，再一次 INSERT 到目標，避免約束／trigger 看到半列。
2. `varchar(max)`、`nvarchar(max)`、`varbinary(max)` 使用對應 staging 型別與 `.WRITE(chunk, NULL, 0)` 追加。來源非 NULL 值先初始化空值，來源 NULL 另行標記，不能把 NULL 與空字串／空 binary 混為一談。varchar staging 明確使用來源 collation，不能繼承 tempdb 的預設定序而改變資料。
3. 文字分段不切斷 surrogate pair，保留換行、尾端空白、單引號與字元語意；以 DATALENGTH 而非 LEN 核對 bytes。`text/ntext/image` 採相應的 max staging 與明確目標轉型，必須有還原測試，不能更改原表 DDL 型別來迴避問題。原生特殊型別使用獨立 codec，不假裝成一般文字。
4. worker 按 reader 欄位次序讀取；小列緩衝超過門檻時，已讀的有界資料也轉入 staging 指令，之後繼續串流，不重新查來源列、不依賴 PK 或 OFFSET。
5. staging 記錄工作／列識別、欄位、chunk 次序、已收 bytes、預期完成數與錯誤／完成狀態。只有所有欄位完整，最後短 transaction 才能執行目標 INSERT 並標記 consumed；缺段、重複、順序錯誤、轉型失敗、重複執行最後批次或 staging 不存在都明確失敗。
6. 每個 batch 自己處理錯誤；TRY/CATCH 與區域變數不能跨 GO。不能只依靠 XACT_ABORT 或客戶端停止選項防止半列插入；即使後續批次被執行，最後完整性 guard 也須阻止錯誤資料進目標。SQL NULL 不能作為轉型失敗的替代結果。
7. 匯入工具必須全檔維持同一 session，支援 GO，錯誤時停止，且不展開來源文字中的 sqlcmd `$(...)`。驗收使用單次 `sqlcmd -i`、`-b`、`-x` 及明確 UTF-8 輸入設定；不把必須同 session 的脚本分成多次連線執行。正式交付文件列出經驗證的實際參數；GUI 匯入只有通過同樣測試後才宣稱支援。
8. 每列 INSERT 完就清掉該列 staging，不累積整張表。匯出端維持有界記憶體；還原端仍需足夠 tempdb／交易記錄與目標儲存空間容納當前大列，不能承諾無限資源。空間不足時，該列不得以殘缺值發布。

以下為設計依據；實際 SQL 驗證結果另記於交付證據，不以本節規格取代：[Microsoft UPDATE .WRITE](https://learn.microsoft.com/en-us/sql/t-sql/queries/update-transact-sql?view=sql-server-ver17)、[GO 與 batch 範圍](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/sql-server-utilities-statements-go?view=sql-server-ver17)。

### 空間資料與後續擴充

- MSSQL 長文字主要是 `varchar(max)`／`nvarchar(max)`，亦可能有既有 `text/ntext`；`longtext` 是 MySQL 的型別名稱。本版只處理 MSSQL。
- 若 WKT 或其他內容實際存在一般文字／binary 欄位，依原欄位型別串流保存，不解析、縮短或簡化其中內容。
- 原生 `sys.geometry` 依 [Geometry 擴充規格](geometry-design.md) 支援 `Serialize → 分段 binary → Deserialize`，保留 native DDL、序列化 bytes、NULL／empty、SRID 與 Z/M；不轉成 WKT／nvarchar，也不修復或簡化圖形。`geography`、user CLR／alias 型別及 spatial index 仍在預檢明確阻擋；含 spatial index 的表連僅資料模式也維持拒絕。跨引擎版本與理論容量上限仍須額外還原驗證。
- 使用者接受遇到極端型別再擴充，因此本版不要求同時支援所有空間／自訂型別，也不把未測試的多 GiB 邊界宣稱為已通過。真正超過型別容量、物件無法保真或環境資源不足時才明確停止，保留原正式檔案。

### 支援與預檢

v1 以一般 disk-based user tables 為主，從 `sys.*` 收集原生定義；不得沿用跨庫 type mapper。至少處理：

- schema/name 正確引用，含空格、Unicode、點號、右方括號等名稱；不把整個 `sales.orders` 引用成單一識別字，也不將目標鎖死在來源 DB 名稱。
- 原欄位型別、長度、precision/scale、nullability、collation、default 與表／欄位 comment。
- Unicode／單引號／換行、NULL 與空值、binary；decimal(38,s) 不經不足精度的 CLR decimal；日期時間保留對應型別的精度與 datetimeoffset offset。
- identity seed/increment 與既有欄位值，適當的 IDENTITY_INSERT；還原後新 identity 不與已匯入資料衝突。不承諾重建來源 identity 配號留下的所有間隙。
- computed 定義／persisted；INSERT 排除 computed 與 rowversion，rowversion 由目標重生且在摘要說明。
- 一般 rowstore PK/UQ/index，保留名稱、key 次序、ASC/DESC、INCLUDE、filter 與適用屬性。FK/CHECK 在資料之後建立，保留其有效／信任狀態。
- 檢視定義用獨立 batch，保留必要 SET 選項、處理所選範圍內的依賴順序。無法讀取／加密／依賴未包含物件時預檢提出，不產生看似完整的結果。

第一版不支援的特殊型別／表／索引特性須在預檢明列並阻擋受影響選取，不可默默轉成其他型別或省略。包含 temporal/ledger、memory-optimized、FILESTREAM、user CLR/alias UDT、Always Encrypted、sql_variant、geography／spatial index、typed XML、partition／特殊索引等；原生 sys.geometry 值已由專用 binary codec 支援。新增支援必須有對應還原證據。

依賴自訂 function、sequence、trigger 或未匯出物件的定義需要具體列出；不猜測可用性。結構模式若選取不完整 FK／view 依賴，要求調整範圍，不用 NOCHECK 隱藏缺漏。僅資料模式明示需要相容且已建立的目標結構，不保證任意既有資料庫都能直接匯入。

## 7. 進度與 ETA 契約

- 以 immutable progress snapshot 回報工作 ID、階段、表、完成／估計列數、完成表數、已寫 bytes、elapsed、速率、可空的 ETA 及估計可信狀態。
- UI Progress 在 UI context 接收；一般更新約 100–250ms 節流，階段／表切換、錯誤及完成立即回報。不每列塞入 message queue。
- 開始與樣本不足時顯示「估算中」；網路／DB 等待或速率劇烈變動時退回估算中或顯示正在等待，不維持假倒數。
- 用最近一段時間的平滑吞吐量估算。不同表列寬與 LOB 差異須納入估计工作量，不以完成表數等權平均。沒有足夠估計值時保持 indeterminate，仍顯示實際已完成值。
- metadata row count 不必然等於快照實際列數；實際大於估計時更新說明，不越過 100% 或提早完成。
- 整體百分比明標估計，包含後續檔案驗證／鏡像工作；階段切換不能重置整體為 0。驗證／鏡像可用實際 bytes 作分階段精確進度。
- 完成前最多 99%；只有要求的步驟皆完成才為 100% 成功。鏡像失敗不得以整體成功覆蓋警告，但本機成功須保留。
- 不保證 ETA 的固定誤差百分比；驗收其資料來源、變動／未知行為與不欺騙的完成判斷。

## 8. 取消、驗證與發布

### 狀態

Idle → Preflight → Reading/Writing → Verifying → Publishing → Mirroring（可選）→ Completed。

可中斷階段收到取消後轉 CancelRequested → 清理 → Canceled。不可重複的短發布區段使用明確互斥；若發布已完成，結果不得再假稱完全未輸出。

### 暫存與驗證

1. 在目標同目錄建立工作專屬的不可碰撞 `.partial` 檔，以 CreateNew 建立；不刪除其他工作或上次未知的暫存檔。
2. writer 記錄 bytes、工作識別、已完成物件及列數，完整成功時才寫完成標记；關閉／flush 成功才進入驗證。
3. 以固定 buffer 重新讀取，核對寫出時的 hash、長度與必要完整性標記。不得 ReadAllText、ReadToEnd，亦不可用可能整行巨大 INSERT 的無界 ReadLine。
4. 驗證表示檔案與本次完整寫出結果相符，不等於 SQL 已在目標伺服器成功執行。真正語意正確性另由還原測試取得。
5. 最後取消檢查之後，無目標時用不可覆寫 move；已有獲同意覆寫的目標時用同磁碟安全 replace。替換不支援或失敗時不退回先刪原檔；回報失敗及可恢復路徑。
6. 確認覆寫後若目標又被其他工作變更，偵測衝突並停止發布，不能覆蓋未確認的新版本。
7. 取消／失敗僅清理本工作暫存檔；清理失敗要列出殘留路徑，不把不完整檔案當成正式產物。日誌不含連線秘密或 SQL 資料值。

### UI 關閉

執行中關閉視窗視同取消請求，等 worker 清理完成再關閉。UI 不直接 Dispose 正在讀取的共享資源；operation ID 阻止舊工作回報污染新畫面。

## 9. 備份鏡像相容性

- 只對原本會 mirror 的備份入口保留鏡像；目的地及保留份數沿用設定，不擅自另建雲端／遠端服務。
- 本機產物發布後，鏡像用工作專屬暫存檔分塊複製並回報 bytes；副本驗證完成才發布並執行既有保留規則。
- 不能先清理舊備份再發現新副本無效。不得擴大既有清理規則至不相關檔案。
- mirror 失敗、取消或目的地不可用：保留已成功本機檔案與既有遠端備份，結果明示「本機已完成，鏡像未完成」，附可採取的下一步。
- 從 mirror 開始就已存在本機正式檔案；取消文案與結果須反映這個發布邊界。

## 10. 驗收與證據

| 項目 | 必須取得的證據 |
| --- | --- |
| 編譯 | 正式方案 Debug／Release 通過，既有 provider 路徑未被不必要變更 |
| 畫面 | WinForms 真正選表、查詢中不凍結、雙層進度、DPI／小工作區、深淺色、取消／關閉生命週期 |
| 大資料 | 超過 2 GiB 的實際產物；來源擴大至少 10 倍時記憶體不按匯出總量線性增加，记录 peak、rows、bytes、elapsed |
| 大欄位 | 跨過舊 16 MiB 門檻的文字／binary 必須走分段且可還原，核心案例至少 64 MiB binary 及超過 16 MiB 的 Unicode 文字；比對 bytes／雜湊、NULL／空值、尾空白、跨 chunk 引號與 surrogate、無 PK、多 LOB 同列。缺段／重複／故意轉型或空間失敗後不得插入半列。超過 1 GiB 與接近型別理論上限的壓力測試另列環境條件與結果，未測不得宣稱通過 |
| 空間欄位 | 原生 sys.geometry 走 Serialize／bounded binary／Deserialize，比對還原前後 raw bytes、SRID、Z/M、NULL／empty，含 >16 MiB 單值；geography 與 spatial index 仍明確預檢拒絕。一般文字中的大型 WKT 保留原文字，不自動轉型、截斷或簡化 |
| 型別與結構 | 在獨立測試 DB 匯入實際產物，驗證精度、offset、Unicode、binary、identity 後續新增、computed、rowversion 語意、constraints／indexes／view |
| 持續寫入 | 用另一連線交易性修改父子表，匯出仍對應同一快照，不混用不同時間點；版本條件不足時預檢停止 |
| 取消與競態 | 讀取／寫入／驗證／鏡像時取消、關閉窗、查詢等待、DB 中斷、磁碟滿、覆寫競態；原檔與結果狀態符合發布契約 |
| SSH | 透過工作自己的 transport 成功、取消、失敗後釋放；不影響主視窗連線 |
| 鏡像 | 成功驗證後保留清理；失敗／取消不清掉舊可用備份，本機產物仍可找到 |

測試來源限明確的隔離／可拋棄 fixture，不在使用者正式 DB 建測試資料或擅改 SNAPSHOT 設定。沒有可用測試 DB 時，實機還原、持續寫入一致性、SSH 與大資料效能必須列 pending，不能由編譯或 fake-provider 測試代替。

保留既有 `ReportExportTests.exe` 防毒隔離邊界：不還原／執行、不變名重跑、不調整防毒。新增功能測試選用可用且未被隔離的正常測試管道；若環境阻擋就明列未驗證，不繞過。

## 11. 交付與流程狀態

- 已交付獨立互動原型；真實匯出程式與隔離 DB 驗證進行中，尚未完成整體交付。
- 本文件已獲同意，具體實作步驟與測試安排見 [implementation-plan.md](implementation-plan.md)。規格核准不擴張為 merge、push 或 release 授權。
- 舊版入口相容、metadata／SQL writer、worker／取消／發布、精靈與進度、實機 fixture 是同一功能的必要工作，不能只接好畫面就宣布完成。
- 開發過程保留既有檔案 BOM／換行、未相關工作樹內容；重要結果追加 `history.md`。

## 2026-10-12 Geometry 擴充

使用者明確要求繼續推進 Geometry `Serialize → 分段 binary → Deserialize`，取代初版對 geometry 值的延後決策。範圍與驗收見 [Geometry 規格](geometry-design.md)／[實作計畫](geometry-implementation-plan.md)；實際 SQL2025 還原、完整回歸與 Debug／Release 編譯已通過，獨立覆核進行中。其餘 provider、geography、spatial index 與既有未驗證環境邊界維持不變。
