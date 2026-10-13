# MSSQL SQL Import Wizard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 以專用精靈安全且有界地執行 mySQLPunk 大型 MSSQL SQL 檔及一般 T-SQL／GO，提供 Punky、錯誤紀錄、取消、可信進度與 ETA。

**Architecture:** 同一唯讀檔案 handle 先完整掃描，再由單一 owned SqlClient session 依序執行 GO batches。reader、native 格式辨識、session adapter、worker、log／ETA 與 WinForms 各有獨立責任，保留既有 exporter 的 LOB／Geometry 協定。

**Tech Stack:** .NET Framework 4.7.2、WinForms、System.Data.SqlClient、既有 MSBuild／smoke runner；不新增套件、WebView 或外部 sqlcmd 執行依賴。

**Spec:** [design.md](design.md)，使用者於 2026-10-13 回覆「好，進實作」核准書面規格。

**Status:** 使用者已明確授權進實作，沿用分工逐項覆核；隔離分支 codex/mssql-import 的 Task1 準備中。進度與技術裁決存於本 worktree 的 `.superpowers/sdd/implementation-plan/progress.md`；實際 DB 驗收仍待 Task6/7。

**Execution:** 保留先前選定的「分工實作並逐項覆核」。一次一位 implementer，各 task 經獨立覆核後才接下一項；最後整體覆核。build、native UI 與 DB tests 由單一 owner 串行執行，避免共享輸出／fixture 衝突。

## Global Constraints

- 保留既有編碼／BOM／換行；不碰 unrelated `mySQLPunk.Desktop/Properties/`，不讀取帳號 token／瀏覽器 session／已存連線或登入設定。
- 只替換 MSSQL 的檔案執行入口；其他 DB 引擎及查詢編輯器不改，`IDatabase` 公開契約不擴張。
- 單一 GO 批次上限為解碼後 32 MiB，即 16,777,216 個 UTF-16 code unit；不是欄位大小上限。不能插 GO／依分號硬切。禁止整檔 bytes／string／AST／batch list。
- 嚴格 UTF-8 有／無 BOM、UTF-16 LE／BE 有 BOM；保留原文及同一 session。支援獨立 GO／GO -- 註解，拒絕 GO n、同分隔行 block comment、SQLCMD 指令與跨 DB 的直接 USE。
- 預設不略過錯誤；略過對象為失敗 GO batch，不是單筆 row。timeout／斷線／log IO／未知交易／native LOB 失敗仍停止，不重連、重試、重播或續傳。
- 專用連線明確指定 Initial Catalog；ConnectRetryCount=0，單一 command，CommandTimeout=0。狀態查詢 5 秒、cleanup 10 秒，取消 10 秒無回應後只嘗試關閉本次 owned connection。
- 不新增全檔外層 transaction、不自動清庫／DROP 使用者物件、不改 SNAPSHOT／recovery model／constraint trust、不 GRANT／KILL。
- 所有走到 EOF 的作業都做 session／交易／digest／已初始化 LOB 終態檢查，包括已略過錯誤者。native LOB 結尾要求唯一 state row、Failed=0、Consumed=1、Sequence=ConsumedThrough。
- error log 預設 `%LOCALAPPDATA%\mySQLPunk\ImportLogs\import-<timestamp>-<job>.log`；略過前落檔並 flush。UI 最近 100 筆或 64 KiB UTF-16 文字，先到者為準，完整路徑可見。
- 進度與 batch／line／byte／error 統計為 Int64；command／result drain／狀態處理完成才更新確認邊界。affected rows 不能標為實際匯入筆數。
- UI 約 200 ms 合併更新；ETA 約 10 秒窗口、至少 3 次樣本與 2 秒觀察，5 秒未確認或長指令／索引／核對中改為未知。
- Punky 自動 loop，聲音首輪一次、可靜音，沒有播放／暫停按鈕。取消、關閉與 late callback 沿用 job generation／owned resource 模式。
- 測試用既有 `mySQLPunk.SmokeTests.exe`，不執行／重建 ReportExportTests.exe，不調整防毒，不 push／merge／release。
- 大型 SQL、DB files、logs、manifest 與原始驗收證據只存 C 槽 local-only，不進 Git。使用者實檔／Transfer 不受 fixture 自動清除權限涵蓋。

## Review Focus

1. 批次間安全查詢會改變 `@@ROWCOUNT`／`@@ERROR`：Task3/4 明訂相容邊界與跨 GO 測試，不能宣稱保存所有 session intrinsics。
2. 一般 SELECT 回傳數百萬列或 64 MiB LOB：Task3/6 逐結果串流 drain，可取消，未 drain 完不能確認 batch。
3. 同批先寫成功再出錯：Task4/6 同時驗證 stop／skip，保留部分寫入說明與所有 SqlError，不虛構 row 成功數。
4. 取消與成功、狀態查詢、COUNT_BIG、log flush 或 form Dispose 競爭：Task4/5/6 不提前宣告取消、不釋放別人的 session、不啟動重疊工作。
5. native DataOnly／偽造或缺失 header/footer/job／失敗但交易為 0：Task1/3/4/6 保守分類、驗證 GUID／終態，不默默改為 generic skip 或完整還原通過。

## Files and shared contracts

所有新型別 internal，位於 `mySQLPunk.lib.MssqlImport`；下列介面是 tasks 交接契約。具體小型 DTO constructor 可採唯讀 properties，但名稱／單位固定。

| 檔案 | 責任／主要型別 |
| --- | --- |
| `mySQLPunk/lib/MssqlImport/MssqlImportModels.cs` | Request／batch／scan／progress／result／error／target／session-state immutable DTO 與 enums |
| `MssqlImportInput.cs`, `MssqlGoBatchReader.cs` | 唯讀 handle、strict decoding／SHA、bounded lexical GO reader |
| `MssqlImportScriptInspector.cs` | native 格式／job／batch role、unsupported 指令與直接 USE 的 lexical 檢查 |
| `MssqlImportLog.cs`, `MssqlImportProgressEstimator.cs` | 完整串流 log、bounded tail、速率／ETA |
| `MssqlImportSession.cs` | owned SqlClient adapter、metadata／transaction／LOB 狀態、result drain、取消／cleanup |
| `MssqlImportService.cs` | 預檢、順序 worker、skip 政策、EOF 驗證與結果組裝 |
| `mySQLPunk/MssqlImportWizardForm.cs`, `mySQLPunk/Form1.MssqlImport.cs` | 四步 UI、MSSQL 入口／目標 settings snapshot |
| `tests/SmokeTests.MssqlImport.{Parser,Worker,Integration,Wizard}.cs` | 沿用現有 partial smoke 類別的測試 |
| `tests/Run-MssqlImportIntegration.ps1`, `tests/Run-MssqlImportTransferTrial.ps1` | 隔離 fixture／實際 Transfer 試跑，兩者的授權與 cleanup 完全分開 |

表格中未寫完整路徑的 backend 檔案皆位於 `mySQLPunk/lib/MssqlImport/`。

- `MssqlImportRequest(Guid jobId, string sourcePath, string databaseName, bool skipSqlErrors = false)`：不含帳密；`JobId/SourcePath/DatabaseName/SkipSqlErrors`。
- `MssqlImportBatch`：`long Number/StartByte/EndByte/StartLine/EndLine`、`string Text`；byte 區間為原始檔案的半開區間，不能以解碼字元數代替。
- `MssqlImportInputException : Exception`：固定 `string Code` 與 `long BatchNumber/Line/ByteOffset`，讓預掃描失敗有穩定診斷。
- `MssqlImportScan`：`long FileBytes/BatchCount/MaxBatchCodeUnits`、`string EncodingName/Sha256`、`MssqlImportScriptInfo Script`；不保存所有批次。
- `MssqlImportScriptInfo`：`Kind { Generic, NativeSchema, NativeData }`、`Guid? NativeJobId`、`long? ExpectedTables/ExpectedRows`、`string SourceReadMode`、`bool HasLobProtocol`；native marker 候選毀損是預檢失敗，不降級 generic。
- `MssqlImportInput.Open(string path, string databaseName = null): MssqlImportInput`；`Scan(CancellationToken, Action<long>): MssqlImportScan`；`ReadBatches(CancellationToken): IEnumerable<MssqlImportBatch>`；執行第二輪可取得 `ExecutionSha256`，物件為 IDisposable 並持有同一 handle。
- `MssqlImportSqlError`：`int Number/State/Class/RelativeLine`、`string Message`；`MssqlImportBatchException` 保留本批全部 `IReadOnlyList<MssqlImportSqlError> Errors` 及 `bool OutcomeUncertain`，原始 exception 不送 UI。
- `MssqlImportSessionState`：`bool IsOpen`、`int DatabaseId/TransactionCount/XactState`；target identity 另含實際 ServerName／DatabaseName／DatabaseId／DatabaseCreateDate／Version／Writable／metadata 可見性／user object count。
- `MssqlImportLobState`：`bool Exists`、`long RowCount/Sequence/ConsumedThrough`、`bool Consumed/Failed`；native identifier 僅能由驗證過的 GUID 產生。
- `IMssqlImportSession : IDisposable`：`ReadTarget(CancellationToken): MssqlImportTarget`、`ReadState(CancellationToken): MssqlImportSessionState`、`ReadLobState(Guid, CancellationToken): MssqlImportLobState`、`ExecuteBatch(MssqlImportBatch, CancellationToken): void`、`CountTargetRows(Action<long,long>, CancellationToken): MssqlImportCounts`、`CancelActiveCommand(): void`、`Cleanup(): MssqlImportCleanup`。同步方法只由 background worker 呼叫；adapter 管理實際 async reader／Cancel race。
- `MssqlImportPreflight`：持有 Input／Scan／Target／LogPath／Passed／固定 Code 的 Issues；IDisposable，重選與關閉時釋放 input 及未用的 job log；Run 原子領取一次 execution ownership，重用同一預檢拒絕，重複 Dispose 安全。
- `MssqlImportService(Func<IMssqlImportSession> openSession, Func<Guid,IMssqlImportLog> openLog)`；`Preflight(MssqlImportRequest, Action<MssqlImportProgress>, CancellationToken): MssqlImportPreflight`；`Run(MssqlImportRequest, MssqlImportPreflight, Action<MssqlImportProgress>, CancellationToken): MssqlImportResult`。Run 再核對 target；factory 每作業開一次專用 execution session。
- `IMssqlImportLog : IDisposable`：`string Path`、`WriteStart(request,scan,target)`、`WriteError(batch,error,action)`、`Flush()`、`WriteEnd(result)`；failure 直接向 worker 冒泡，不能吞掉後繼續。`MssqlImportLogTail.Add(string)`／`Snapshot(): string`、`long TotalErrors`。
- `MssqlImportProgress`：`Guid JobId`、`Stage { Scanning, Executing, Verifying, Stopping, Finished }`、`long ScannedBytes/ProcessedBytes/SuccessfulBytes/SkippedBytes/SuccessfulBatches/FailedBatches`、目前 Batch／固定 Phase／可選 TableName、TimeSpan Elapsed／TimeSpan? Remaining／DateTime? EstimatedFinish；bounded string RecentErrorText／LogPath、long TotalErrors、bool LogTruncated。`MssqlImportProgressEstimator.Observe(progress, DateTime utcNow)`／`Estimate(DateTime utcNow): MssqlImportEstimate`。
- `MssqlImportResult`：`Status { Completed, CompletedWithErrors, Failed, Cancelled, CleanupUncertain }`、統計／LastAcknowledgedBatch／UncertainBatch／LogPath／Cleanup／ValidationSummary；`MssqlImportCounts(long tables,long rows)`。`MssqlImportCleanup` 記錄是否確認 command 結束、session 釋放、未提交交易 cleanup 與固定 issue code，不把 Dispose 呼叫等同伺服器 rollback 已完成。

## Planning decisions that must remain explicit

- 現有 native header 沒有 mode/job manifest：使用 exporter 固定 Source read mode／空庫提示、實際 lexer token 及 footer 共同分類；LOB `#mp_<guidN>_s/_v/_c` 必須與 footer job 一致。不新增 exporter 格式要求讓使用者現有 4.17 GiB 檔案失效。
- 現有 `OpenMssqlSession(settings)` 已開線，不能事後補 ConnectRetryCount=0。Task3 新增 importer 專用 pre-open option；其他 callers 維持原連線行為。
- 一般腳本可跨 GO 保留 temp tables／SET／IDENTITY_INSERT／transaction；為了偵測交易與目標變化，插入的狀態查詢可能改變 `@@ROWCOUNT`／`@@ERROR`。v1 不保證這兩個 statement intrinsics 跨 GO 保持原值，文件與 generic-script 預檢說明需明示；這不影響 exporter 已產生的腳本。需要在同一 batch 保存這些值的腳本應自行先存入變數／temp table。語義依據：Microsoft [@@ROWCOUNT](https://learn.microsoft.com/en-us/sql/t-sql/functions/rowcount-transact-sql?view=sql-server-ver15) 與 [@@ERROR](https://learn.microsoft.com/en-us/sql/t-sql/functions/error-transact-sql?view=sql-server-ver15)。
- 現有 export integration runner 硬編碼的 ignored fixture manifest 不在目前 master。新 runner 採明確 `-AuthorizationPath`，驗證既有已授權專用 LocalDB 名稱／C 槽 fixture root／owner token；不把缺檔當已授權成功，也不去讀 Codex 帳號設定找憑證。

## Task 1: Bounded GO reader、檔案身份與 native 格式預掃描

**Files:** Create `MssqlImportModels.cs`, `MssqlImportInput.cs`, `MssqlGoBatchReader.cs`, `MssqlImportScriptInspector.cs`, `tests/SmokeTests.MssqlImport.Parser.cs`。Modify `mySQLPunk/mySQLPunk.csproj`, `tests/SmokeTests.cs`, `tests/Run-SmokeTests.ps1` 的 explicit source registration。

**Interfaces:** 產出上述 Batch／Scan／ScriptInfo／Input；inspector `Observe(MssqlImportBatch)`、`Complete(): MssqlImportScriptInfo`，保留有限 metadata，不保存完整 batches。其餘 models 在使用它的 task 補齊，不提前加入未使用的空架構。

- [ ] **Step 1: 加入並跑失敗的 parser tests。** `MssqlImportParserPreservesContexts` 驗證字串／雙引號／方括號／巢狀 comment 中 GO 不拆；1／2／3／7-byte buffer 切在 UTF-8、BOM、CRLF、`''`、`]]` 中間仍原文相同。`MssqlImportParserLimits` 的斷言：16,777,216 code unit 的 Scan 成功且 MaxBatchCodeUnits 相等；16,777,217 時拋 MssqlImportInputException、Code="BatchTooLarge"。
- [ ] **Step 2: 實作固定 byte／decoder buffer、bounded current batch 與 lexical line candidate。** 不用無界 ReadLine、ReadAllText／ReadAllBytes／ReadToEnd；strict decoder 的 EOF flush 要抓無效序列。明訂 EndByte 包含目前 batch 的 GO delimiter，但 SQL Text 不含 GO。純註解／空白尾段不送空 command，其 byte 覆蓋只在 EOF 驗證通過後才計入最終 ProcessedBytes=FileBytes。
- [ ] **Step 3: 補 unsupported／native tests 並實作 inspector。** GO n、GO block comment、SQLCMD 真指令、跨庫直接 USE、未閉合 EOF、無效 encoding 拒絕；字串中的相同文字不拒絕。同 DB USE 在傳入 databaseName 且 decoded identifier 完全 Ordinal 相等時可接受；unknown target 或不同大小寫也保守拒絕，避免 case-sensitive server 的跨庫同批寫入。native Schema／DataOnly／無 LOB、正確／缺失／重複／溢位 footer、錯 job、偽造 GUID、header 不一致都要測；native candidate 不可因缺 footer 轉 generic。合法 Generic 不要求 footer。
- [ ] **Step 4: 以同一 handle 掃描→rewind→執行第二輪，測 SHA 一致與 FileShare.Read 阻止一般寫入／刪除；檔案重選 Dispose 後可寫。** 原始 byte／line offset、無 GO 的最後批次、僅註解檔不產生可執行空 command；大檔統計使用 Int64。
- [ ] **Step 5: 驗證並提交精確檔案。** `MYSQLPUNK_SMOKE_FILTER='MSSQL import parser'` 跑 `tests/Run-SmokeTests.ps1 -Configuration Debug`，必須 matched tests 全過；`git diff --check` 通過。Commit `feat: add bounded MSSQL GO input preflight`。

## Task 2: Durable error log、bounded tail 與進度／ETA

**Files:** Create `MssqlImportLog.cs`, `MssqlImportProgressEstimator.cs`, `tests/SmokeTests.MssqlImport.Worker.cs`；補齊 Models 的 error／progress／result contracts，註冊 sources。

**Interfaces:** 產出 IMssqlImportLog／Tail／Estimator 及 terminal DTO；log 可注入 Stream factory 與 clock，測 IO 失敗不需修改檔案權限。Estimator 僅消費已確認 progress，不推測 affected rows。

- [ ] **Step 1: 加入失敗 tests。** `MssqlImportLogRetainsAllErrors` 寫入 1,283 個多 SqlError detail 的事件，磁碟都有、UI ≤100筆且 ≤32,768 chars；單筆 >64 KiB 不截完整 log。唯一檔名 CreateNew、UTF-8、包含 batch／line／byte／codes／path／job，沒有整個 SQL 或連線字串。
- [ ] **Step 2: 實作串流 log 與 tail。** Start 必須在 DB 寫入前 flush；每次準備 skip 前 flush；create／append／flush／end／Dispose fault 均可被 worker 看見。TotalErrors 不隨 UI 淘汰減少；不累積所有失敗批次，log 存開啟／資料夾／複製可用的真實路徑。
- [ ] **Step 3: 加入並實作 ETA tests。** 暖機少於 3樣本或2秒不估；約10秒窗口；5秒停滯／Indexes／Verifying 回未知；有 skip 時 Processed=Successful+Skipped、成功比例獨立；read-ahead 和執行中 batch 不算確認。測 Int64 >2 GiB／高 batch 序號、clock injected、200 ms throttle 不漏最終狀態。
- [ ] **Step 4: 驗證 fault assertions。** `FlushFailsBeforeSkip` 預期 `NextBatchCalls==0`；密集錯誤10倍數量下 tail 記憶體固定；UI 文案顯示完整路徑且不冒稱所有資料成功。
- [ ] **Step 5: 跑 worker filter、提交。** `MYSQLPUNK_SMOKE_FILTER='MSSQL import worker'`、Debug smoke 全 matched tests 通過。Commit `feat: add MSSQL import logging and progress estimates`。

## Task 3: Import-only owned session、target preflight 與 bounded result drain

**Files:** Create `MssqlImportSession.cs`，補齊 Models 的 target／session／LOB／counts／cleanup；Modify `mySQLPunk/lib/ConnectionConfigurationService.cs`, `mySQLPunk/lib/ConnectionOpenService.cs` 的 importer-only overload；tests Worker／Parser 與新 `tests/SmokeTests.MssqlImport.Integration.cs`。

**Interfaces:** `ConnectionOpenService.OpenMssqlImportSession(Dictionary<string,object>): MssqlOwnedSession`，在開線前套 `ConnectRetryCount=0`、`MultipleActiveResultSets=false`；原 `OpenMssqlSession` 行為維持。`MssqlImportSqlSession(MssqlOwnedSession)` implements IMssqlImportSession，owner 只釋放一次。

- [ ] **Step 1: 加入失敗 tests。** importer builder 明確 Initial Catalog／retry0／MARS false；舊 caller 既有值不變。使用 fake owner 驗證取消、初始化失敗、Dispose race 只有本次 session／SSH lease 被釋放，樹 provider 不受影響。
- [ ] **Step 2: 實作唯讀 target／state／LOB probes。** server／DB id／create_date／版本／writable／metadata 可見性／object count；必要能力不以 sysadmin 或 ALTER DATABASE 一票否決。NativeSchema 非空／無法確認空庫擋下；Generic／NativeData 不套空庫要求。所有 identifiers quote，LOB temp name 只由 Guid.ToString("N") 組成；state query 5秒，lookup 不能讀任意帳號設定。
- [ ] **Step 3: 實作原文 ExecuteReader 與所有 result drain。** `CommandBehavior.SequentialAccess`，涵蓋 ExecuteReader／Read／NextResult／reader Dispose 的全部 SqlError；不建 DataTable／回傳資料集合。CommandTimeout0，token＋Cancel 包住啟動與後續讀取，保留目前 command 的可靠 ownership。
- [ ] **Step 4: 實作及測 cancellation／cleanup seam。** 不在 active reader 尚在跑時發第二條 status query；cleanup 獨立10秒，不使用已取消 token；關閉本 owner 不宣稱 rollback 已完成。一般 GO 跨 session 測 temp／SET／identity／transaction；另測 @@ROWCOUNT／@@ERROR 邊界並更新 generic-script 說明，避免不實相容宣稱。
- [ ] **Step 5: 跑 worker／parser filters，提交。** native live cases 暫列 Task6 執行，不能以 fake 通過冒稱 DB 通過。Commit `feat: add isolated MSSQL import session adapter`。

## Task 4: Sequential worker、recoverable skip、EOF checks 與取消結果

**Files:** Create `MssqlImportService.cs`；補 Worker tests；Models／Input／Log 僅做必要整合，不改 exporter 協定。

**Interfaces:** 完成 Service.Preflight／Run；Preflight 保留 input／scan／target identity／已建立 log；Run 在新專用 execution session 重查相同 server／DB id／create_date 與能力，所有 batches 同線。

- [ ] **Step 1: 先寫失敗的 fake-session tests。** `DefaultStopsAtFirstError`、`SkipContinuesOnlySafeBatch`：未勾後續0calls；勾選一般class11–16、前後交易0／XactState0、same DB、無LOB pending、log flush成功才下一批。任何 SqlError 嚴重／timeout／傳輸／不明 outcome 不可 skip，不比對 message 語言。
- [ ] **Step 2: 實作預檢→確認→執行流程。** 所有 input encoding／batch／native／目標／log 可寫性問題於第一個 DB write 前阻擋，parser失敗時execution factory calls=0；不同檔案／target／skip設定令預檢失效，ReusedPreflightIsRejected 測試第二次 Run 不送 SQL。一次一批，結果與 state 查明後才確認 bytes；目前batch可能部分生效，分開成功、失敗與未知批次。
- [ ] **Step 3: 實作 native batch role／state gate 及 EOF。** native LOB initialization／assemble／commit 任一 error fatal，即使 XACT_STATE0；普通batch遇錯也查已初始化 native state。每個走到 EOF 的作業都查交易、identity、digest、LOB唯一state／Failed0／Consumed1／Sequence=ConsumedThrough，包含 skip／DataOnly；pending／missing／wrongjob 不得 amber完成。無錯 NativeSchema 才逐表 COUNT_BIG 對 footer tables／rows，核對可取消。
- [ ] **Step 4: 實作取消／cleanup terminal tests。** 先 Stopping、不排下一批；Cancel10秒無回應只關本 session；cleanup10秒限時。自然 EOF 未結交易、取消在成功回應邊界、狀態查詢／COUNT_BIG／log flush 中取消、cleanup未確認都測。已失敗的原始結果不能被 cleanup exception 覆蓋；若最後 log失敗要顯示存檔未完整。
- [ ] **Step 5: 跑 worker filter、提交。** `PartialBatchEffectsAreNotReportedAsRows`、`SkippedRunStillChecksEofState`、`NativeLobPendingAtEofFails`、`NoReconnectOrReplay` 均通過。Commit `feat: execute MSSQL import batches with explicit outcomes`。

## Task 5: Four-step native wizard、Punky 與 MSSQL routing

**Files:** Create `mySQLPunk/MssqlImportWizardForm.cs`, `mySQLPunk/Form1.MssqlImport.cs`, `tests/SmokeTests.MssqlImport.Wizard.cs`；Modify Form1.cs 的 ImportSqlWithDialog、csproj／smoke source registrations；只必要時局部擴充既有 theme helper。

**Interfaces:** `MssqlImportWizardForm(MssqlImportRequest initialRequest, Func<MssqlImportService> serviceFactory)`；Form1 `OpenMssqlImportWizard(TreeDatabaseTarget)`。沿用 PunkyReportLoadingControl.BeginExport／EndExport／SetRunning／Muted；routing 測試可注入 wizard runner，不需真連線。

- [ ] **Step 1: 先寫失敗 wizard tests。** 四步導覽、未預檢／有阻擋不能開始、確認target必勾、skip預設false；重選檔案／目標會失效舊預檢；關閉釋放 input。所有檔案入口在 MSSQL 先路由新 wizard，其他引擎與查詢編輯器維持既有呼叫。
- [ ] **Step 2: 實作符合核准原型的 WinForms layout。** 顯示實際 server／db、檔案size／編碼／native摘要、READ COMMITTED警告、一般腳本intrinsics邊界與部分寫入說明；結果明示實際核對範圍。log textarea ReadOnly，最近100／64KiB與總數／fullpath，open／folder／copy操作真實檔案。
- [ ] **Step 3: 接 background worker 與單一 cancellation owner。** jobId＋generation 擋 late callback；timer約200ms拉取bounded snapshot；忙碌時不重入、FormClosing轉取消，收尾後才關窗；Disposed UI 不被 callback更新。Punky預設loop、first-cycle音效／靜音、無播放／暫停按鈕。
- [ ] **Step 4: 執行 native UI tests。** STA＋WindowsFormsSynchronizationContext、form.Show／PerformClick／bounded DoEvents；深淺色、高 DPI、縮放後取消可到達、error截段、停止中與終態、close/complete race。若使用 preview，只新增 `MYSQLPUNK_MSSQL_IMPORT_WIZARD_PREVIEW/THEME` 且不開真DB，不能混用 export preview knob。
- [ ] **Step 5: 跑 wizard filter及路由回歸、提交。** 把 native screenshots 存 `docs/design/mssql-import/snapshot/`，與原型JPEG分開命名；記錄動畫視覺／聲音哪些是實際人工確認。Commit `feat: add MSSQL SQL import wizard`。

## Task 6: Authorized isolated DB roundtrip 與 fault acceptance

**Files:** Complete `tests/SmokeTests.MssqlImport.Integration.cs`，Create `tests/Run-MssqlImportIntegration.ps1` 及必要小型 `tests/MssqlImportFixtures/` SQL；記錄 `docs/design/mssql-import/verification.md`。大型生成檔與 evidence 不進 Git。

**Interfaces:** runner 參數 `-Run -AuthorizationPath <manifest> -Profile Core|Lob|Geometry|Faults|Memory|All -OutputDirectory <C-path>`。授權必須解析成 `(localdb)\mySQLPunkExportTest_20261012`、明確C fixture root及owner token；沒有有效manifest不得寫DB。runner 設定並還原 `MYSQLPUNK_MSSQL_IMPORT_INTEGRATION/SERVER/OUTPUT/AUTH/PROFILE`，filter固定 `MSSQL import integration`，要求實際 profile receipt，不能把未啟用測試當 live pass。runner 不自動啟動不明instance；必要時由controller啟動先前已核准的專用名稱。

- [ ] **Step 1: 補 runner guard 失敗 tests，再實作授權／cleanup保護。** 無-Run／錯server／錯root／reparse／非空output／不足20GiB皆不寫；GUID資料庫與實體檔、owner.token＋DB owner extended property雙重驗證才可清。Existing export runner缺manifest不可默默跳過；明確AuthorizationPath，必要時從已核准fixture重新建立可審查manifest，不擴張到Transfer。
- [ ] **Step 2: Core profile。** 新建owned source／target，以現有 exporter產生SQL，再由新 importer本體還原；比對表／欄位／型別／identity／PK／indexes／FK trust／view／table與column extended properties。General腳本含GO、temp／SET／transaction、SELECT多result；無外部sqlcmd作SUT，獨立驗證可另行使用。
- [ ] **Step 3: Lob／Geometry profiles。** 64MiB binary、18MiB Unicode、NULL／empty／XML／Geometry含SRID/Z/M/curves與invalid-but-storable；以串流rawbytes/hash及metadata驗證。native DataOnly也測；注入missing／duplicate／reordered／replayed chunk、Failed1但交易0、missingstate、pendingEOF及final INSERT constraint／Deserialize失敗，即使skip也停止且下一row不寫。
- [ ] **Step 4: Faults／Memory profiles。** 一般batch先INSERT再失敗的stop／skip；所有SqlError落檔；XACT_ABORT／未結交易／斷線／有限timeout seam／logcreate-append-flush-end失敗；取消在resultdrain／stateprobe／COUNT_BIG／close race。結果集數百萬列與64MiB回傳LOB應bounded drain。相同batchshape生成256MiB與2.5GiB串流檔，記錄x64／peak private bytes／working set；暖機後10倍檔案不能線性10倍成長，差異超過256MiB須調查並修復，不能只增加記憶體上限。
- [ ] **Step 5: 各profile通過後提交tests及verification摘要。** 記錄命令、實際matched tests、SQL版本、耗時／artifactsize／hash／cleanup／memory／取消結果；失敗不寫pass，無法驗證明列邊界。Commit `test: verify native MSSQL import roundtrips and failures`。

## Task 7: SQL2019 Transfer 實檔試跑、整體驗證與交付

**Files:** Create `tests/Run-MssqlImportTransferTrial.ps1`, `docs/MSSQL_SQL_IMPORT.md`；Update verification.md、既有README適當入口／history（ignored）及plan完成狀態。產品修正僅限實際驗收發現的問題並覆核。

**Interfaces:** trial runner 必須明確 `-Run -Server localhost -Database Transfer -InputFile C:\Users\johnho\Desktop\Transfer_dump_20261013_165730.sql -OutputDirectory <C-path>`；Windows整合登入，實際server必須`JOHN\MSSQL2019`、version15.x、compat150、ONLINE／READ_WRITE且NativeSchema空庫。無fixture drop／cleanup／recovery設定入口。runner 設定並還原 `MYSQLPUNK_MSSQL_IMPORT_TRIAL/SERVER/DATABASE/INPUT/OUTPUT`，由 Integration partial 中註冊的 `MssqlImportTransferTrial` 執行，filter `MSSQL import Transfer trial`；沒有 trial opt-in 時明確 skipped，-Run 時必須驗證真正執行 receipt，不能以 skipped 當驗收。

- [ ] **Step 1: 先測trial guard再唯讀重查。** 目標已非空、身份／DB create_date變化、檔案不符、空間不足或權限不足時只呈現實際狀態，不能自動清庫、換帳號或重試。記錄source完整預掃描／SHA、batch數／最大batch；不要複製或公開服務這份4.17GiB檔案。
- [ ] **Step 2: 依使用者已有授權執行一次本機實檔。** default-stop，log與監測在C；記錄耗時、CPU／peakmemory、DBdata/log成長、最後位置、實際完成數／錯誤；READCOMMITTED來源的FK失敗不得改NOCHECK。失敗部分保留，先回報具體狀態，額外清除／重跑需使用者針對該狀態授權。
- [ ] **Step 3: 核對結果。** 126 tables／3,247,785 rows為檔案footer預期，COUNT_BIG與實際objects／indexes／constraints／views／comments狀態獨立記錄；不把job完成當資料落地、不把count相同當逐欄hash相同。使用者未人工確認的聲音與操作體驗列pending。
- [ ] **Step 4: 跑最終建置與既有回歸。** 清除本task filter後 `powershell.exe -NoProfile -File tests/Run-SmokeTests.ps1 -Configuration Release`；`./build.bat debug`、`./build.bat release`；AutoReleasePolicy通過。僅在新修正／失敗才重跑相關profiles；不反覆無理由全量還原。全分支一次fresh reviewer，再對實質fix做一次scoped覆核。
- [ ] **Step 5: 文件、歷程與分支checkpoint。** docs說明支援GO/encoding/32MiB batch與LOB無關、generic intrinsics限制、skip／partial effects／logs／取消／ETA／需信任來源；列實際本機證據與未驗證production邊界。保留隔離worktree，精確stage，未獲本次授權前不push／merge／release。Commit `docs: document MSSQL SQL import and acceptance evidence`。

## Commands and execution setup

實作開始時使用 `using-git-worktrees` 檢查已有隔離環境，再優先使用 Codex managed worktree，ref須包含本計畫checkpoint；不可從較舊remote branch遺漏已核准設計。不覆寫primary checkout的untracked檔。D槽空間偏低，managed worktree／生成fixture／build暫存優先C槽，先核對實際容量。

本專案新source與test partial都要登錄explicit清單。測試需匹配至少一項，filter沒匹配退出2不得當成功；第一次失敗退出1。測試命令範例（執行owner須保存並還原既有環境值）：

```powershell
$env:MYSQLPUNK_SMOKE_FILTER = 'MSSQL import parser' # 或 worker／wizard
powershell.exe -NoProfile -File tests/Run-SmokeTests.ps1 -Configuration Debug
Remove-Item Env:MYSQLPUNK_SMOKE_FILTER
powershell.exe -NoProfile -File tests/Run-SmokeTests.ps1 -Configuration Release
.\build.bat debug
.\build.bat release
```

每task依序記錄BASE／實作commit／測試證據／diff package／覆核結論。只修正實質spec或correctness問題，不做無意義review循環；user／production資料不拿來做破壞式fixture。依使用者明確進實作的授權，沿用分工逐項覆核方式，不再重問執行授權或方法。
