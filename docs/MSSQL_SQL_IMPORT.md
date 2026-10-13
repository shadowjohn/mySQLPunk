# MSSQL SQL 檔匯入

從資料庫樹選定 MSSQL 資料庫，再使用既有「執行 SQL 檔」入口，即可開啟原生四步匯入精靈。
本流程適用 mySQLPunk 匯出檔與本版支援的一般 T-SQL；請先確認腳本來源可信。

## 四步操作

1. **檔案與目標**：選擇 SQL 檔，核對檔案大小及已選資料庫。
   目標欄位唯讀；若要換目標，關閉精靈，在資料庫樹另選資料庫，再重新開啟。
   實際解析的 server／instance／database 會於預檢後顯示，請以該識別為準。
2. **匯入檢查**：完整串流掃描檔案，檢查編碼、GO、批次大小、native 摘要與目標條件。
   預檢只讀來源檔與資料庫 metadata，不執行檔案內的 SQL；失敗時不能進入執行。
   完成前請等待掃描進度；更換檔案或錯誤政策後須重新檢查。
3. **確認執行**：核對實際目標、來源、錯誤政策與完整 log 路徑。
   勾選「已確認目標資料庫，並了解異常時可能保留部分資料。」後按「開始匯入」。
   開始前仍會重查目標狀態；預檢通過不保證任意 SQL 都有權限或能成功。
4. **執行與結果**：查看已處理範圍、批次、行號、時間、錯誤及最後結果。
   結束後可開啟 log、開啟資料夾、複製路徑，或按「重新檢查匯入」。
   重新檢查會回到預檢，不會直接重播失敗批次。

來源檔以唯讀方式保持開啟，預檢後以同一 handle 重新讀取；來源內容不改寫。
開始前請自行確認備份與目標現況，尤其是已有資料的一般腳本或資料型匯出。

## 進度、Punky 與聲音

Punky 自動循環動畫；每次實際執行只在首輪播放一次聲音。
使用「靜音／開啟聲音」切換；取消靜音不會重播，沒有動畫播放／暫停按鈕。
預檢進度與執行進度分開；執行 bytes 只在批次回應並處理完成後確認。
讀檔預取的位置不是已匯入位置；成功與失敗／略過的範圍分別統計。
最後只有註解的 bytes，也要等 EOF 安全檢查通過後才確認。
略過錯誤時，「已處理檔案範圍」到 100% 仍可能有失敗批次。
ETA 由最近確認的 bytes 速率估算，需先取得足夠樣本。
長指令、索引、約束、結果核對或久無新進度時會顯示計算中／目前指令執行中。
請勿把讀完整份檔案或 ETA 歸零當成匯入與核對已完成。

## 錯誤政策與部分寫入

預設遇到 SQL 錯誤停止。可勾選「遇到 SQL 錯誤時，略過失敗 GO 批次並繼續」。
這是略過整個失敗 GO 批次，不是略過單一 row；同批較早已生效的語句可能保留。
只有一般 SQL 錯誤、原連線與目標正常、前後無交易、native LOB 狀態安全且 log 已保存時才可略過。
timeout、取消、斷線、嚴重錯誤、結果不明、交易／LOB 異常及 log 寫入失敗仍停止。
精靈不自動重試、不支援斷點續傳，也不替整份腳本加上外層 transaction。
腳本自行開啟的交易照原順序處理；跨 GO 交易遇錯不能自動略過。
成功批次的回應不等於其中交易已提交；已提交變更不會因後續停止而整份撤銷。

## 支援的 SQL 檔格式

- 嚴格 UTF-8（有／無 BOM），以及有 BOM 的 UTF-16 LE／BE。
- 無效解碼序列拒絕執行；SQL 文字中的邏輯 U+0000 不支援，會回報 EmbeddedNul。
  它可能是沒有 BOM 的 UTF-16，也可能是 UTF-8 中的 literal NUL，不能一概當成編碼錯誤。
- GO 不分大小寫，須是字串、identifier 與註解外的獨立一行；可有前後空白。
- 支援 `GO -- 註解`；字串／註解裡的 GO 保持原文，不送分隔用的 GO 給 SQL Server。
- 不支援 `GO n`、同一 GO 分隔行的 block comment、SQLCMD 指令或變數展開。
  `:r`、`:setvar`、`:connect`、`!!` 等有效位置的指令會被預檢拒絕。
- EOF 的最後一個非空批次會執行；未閉合字串、identifier 或 block comment 會被預檢擋下。
- 不用分號拆開任意 T-SQL；單一 GO 批次解碼後上限為 32 MiB（16,777,216 UTF-16 code units）。
  超過上限會在來源 SQL 寫入前停止；請調整來源產生方式，勿任意插 GO 破壞 SQL 語意。

32 MiB 是批次界限，不是 LOB 欄位大小限制，也不是整個程序的記憶體上限。
mySQLPunk 既有文字／binary／Geometry 分段協定保持原樣，使用同一 session 組裝與驗證。
缺失、重複、亂序、未消耗或 Failed 的 native chunk 狀態不能靠略過錯誤當成成功。

## Session 與目標邊界

全部 GO 批次依序使用同一專用 session，保留 SET、#temp、IDENTITY_INSERT 與交易連續性。
批次間必要的目標／交易探測可能改變 `@@ROWCOUNT` 與 `@@ERROR`。
若腳本需要這兩值，請在同一批次先存入變數／temp table；本版不保證跨 GO 保留。
直接 `USE` 的解碼目標必須與選定資料庫完全相符（Ordinal，含大小寫）；其他直接 USE 會被預檢拒絕。
三段名稱、動態 SQL 或程序仍可能存取其他資料庫。
請確認腳本可信；本功能不是 SQL sandbox，也不提供完整 SSMS／sqlcmd 相容環境。
精靈不自動 GRANT、建立／清空／覆寫資料庫、變更資料庫設定、放寬約束為 NOCHECK 或 KILL session。
執行中的腳本仍依登入權限變更資料；上述邊界不是任意 SQL 的隔離保證。

## 取消與清理

按「取消匯入」或執行中關閉視窗會停止排程並嘗試取消目前 command。
「正在停止…」表示仍在等待實際結束，不能當成已取消，也不能立即開始另一輪。
取消約 10 秒未得到回應時，會嘗試關閉本次擁有的連線；仍須等待實際 unwind／清理證據。
清理包含嘗試 rollback 本 session 未提交交易及釋放 command、session、來源 handle 等資源。
若釋放仍 pending 或執行／rollback 結果無法確認，結果會明示 CleanupUncertain 與診斷。
Cancelled 也不表示整份還原到執行前；已提交或部分生效的資料可能保留。
失敗／取消後先依 log 核對目標現況；精靈不自動清除部分物件或重跑。

## 完整錯誤紀錄

每次建立獨立的新檔，不覆寫舊 log：
`%LOCALAPPDATA%\mySQLPunk\ImportLogs\import-<timestamp>-<job>.log`。
來源 SQL 開始寫入前須已成功建立 log；追加或 flush 失敗就停止，不能宣稱完整保存。
完整 log 保留所有 SQL error detail、批次／來源座標、採取動作、狀態探測與最後清理結果。
來源執行錯誤與 Probe／Cleanup 錯誤分開標示；後兩者不能冒稱來源腳本行號。
畫面錯誤區唯讀，只保留最近 100 筆或 32,768 UTF-16 code units（先到者為準）。
單筆過長也可能截斷；截段提示與總錯誤數不代表完整 log 遺失，請查看完整路徑。
「開啟 log」「開啟資料夾」「複製路徑」使用實際已建立的檔案路徑。
log 不附整段 SQL／LOB 或密碼，但 SQL Server 原始錯誤訊息可能含資料值，請妥善保存。

## 如何理解完成結果

NativeSchema（含完整結構的 native 匯出）要求已知空庫、完整 metadata 可見性及可寫目標。
需要建表時也須具 CREATE TABLE 能力；原有 schema 本身不代表庫內已有使用者物件。
完整 metadata 採保守證明：dbo，或資料庫 VIEW DEFINITION；SQL major ≥16 的非 dbo 還需 VIEW SECURITY DEFINITION。
非 dbo 的有效身分若有任何明確 DENY，也可能擋下 NativeSchema，即使該 DENY 與可見物件無關。
Generic／DataOnly 可保留可見性警告，再由實際 SQL 權限判定；沒有一律要求 CREATE VIEW。
無錯誤的 NativeSchema 會核對實際 user table 數與逐表 COUNT_BIG，對照 footer 摘要。
Generic／DataOnly 無同等內容 manifest；成功只聲明批次完成與必要安全檢查，未提供資料內容核對。
所有模式仍要通過適用的 EOF、交易、目標、digest 與 native LOB 檢查。
「執行完成，但有錯誤」不等於完整還原通過；請核對失敗／略過及未確認批次。
表數／筆數與 footer 相符也不是逐欄值或來源資料庫 hash 相等的證明。
來源 READ COMMITTED 的跨表不一致可能造成後續外鍵失敗；精靈不自動刪資料或放寬約束。

## 已有證據與待驗收

[設計與相容邊界](design/mssql-import/design.md)；[詳細驗證紀錄](design/mssql-import/verification.md)。
專用 LocalDB 上已匹配 65 個實際 PASS case receipts，包含大 LOB／Geometry、錯誤與取消邊界。
已測 64 MiB binary、18 MiB Unicode、>16 MiB Geometry；測試資料另有 bytes／hash／metadata 比對。
約 256 MiB／2.5 GiB 相同批次形狀檔案已量測記憶體；這是該 fixture 的觀測值，不是通用上限。
SQL Server 2019／Transfer 已實際試跑一次，於第 1,481 批因日期字串轉換錯誤 241 停止；未完整還原。
已確認前 1,480 批成功；停止後唯讀獨立核對為 126 張表／46 筆，不符合來源摘要 3,247,785 筆。
既有匯出器將 legacy datetime／smalldatetime 寫成七位小數；現已修正為三位，並通過獨立 Core 日期 bytes 比對。
原始大檔與 Transfer 部分狀態、一次性 marker／log 均保留，沒有修改來源、清庫或重跑；修正不代表原始大檔已通過。
實際音訊聽感、實體高 DPI／鍵盤操作與 production 驗收仍未確認；本機證據不延伸為 IIS／DB 生產驗收。

註：Task7 一次試跑 harness 的 64 GiB 可用空間入口、<20 GiB 時取消與固定 marker 是該試驗的防護條件。
該 harness 的私有來源路徑與限制不是一般精靈的 UI 限制；普通匯入依上述檔案與目標檢查進行。
