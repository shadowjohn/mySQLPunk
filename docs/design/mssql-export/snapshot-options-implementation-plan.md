# Optional MSSQL SNAPSHOT Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 匯出預設走一般 READ COMMITTED，使用者可選擇有權限的 SNAPSHOT；自動設定管理保留原狀並明確回報復原結果。
**Architecture:** 保留現有 native wizard、owned DB/SSH session、單一資料交易、逐表串流及安全發布。新增獨立 capability、credential-free recovery journal 與 snapshot lifecycle；來源交易結束後先完成有限設定清理，再釋放連線與執行本機階段。
**Tech Stack:** .NET Framework 4.7.2、WinForms、System.Data.SqlClient、既有 Newtonsoft.Json、MSBuild、既有 smoke/LocalDB harness；不加套件。
**Spec:** [snapshot-options-design.md](snapshot-options-design.md)，使用者於 2026-10-13 明確核准。
**Status:** 2026-10-13 已完成 Task1–5 實作與逐項覆核；Task6 七項隔離 profile、194組 Release smoke、AutoReleasePolicy、Debug/Release build 通過，證據見 verification.md。Task6 與全分支獨立覆核待完成，未 merge/push/release。
**Execution:** 延續已選定的分工實作與逐項覆核；完成每項後再交給下一位 implementer，最後整體覆核。所有 build/DB/native UI 執行由單一 owner 串行控制。
**Checkout:** `D:/mytools/mySQLPunk/.worktrees/mssql-export`，`codex/mssql-export-preflight-compat`；產品基底 `34024b7`，設計 checkpoint `4286d57`。

## Global Constraints

- 預設否；缺 ALTER 權限時選項停用，即使資料庫原本 ON 也是如此。此為已核准產品政策。
- 一般模式明確使用 READ COMMITTED；不執行 ON/OFF、不改 RCSI、不用 NOLOCK；SchemaOnly 不開資料交易。
- SNAPSHOT 原本 ON 保持 ON；只有本次由 OFF 啟用才嘗試恢復 OFF。啟用限時60秒，可取消；設定清理使用獨立30秒期限。
- 禁止 KILL、ROLLBACK IMMEDIATE、SINGLE_USER 或終止其他連線作為產品清理手段；不在失敗後換身分／重連去執行 OFF。
- 設定切換前落地復原紀錄；未知結果不得當作 OFF。來源讀取與設定清理結果各自保留，不掩蓋取消／原始錯誤／鏡像結果。
- 原檔保護、bounded streaming、Geometry/LOB 原始值、可信任外鍵 WITH CHECK、metadata fingerprint、必要讀取權限保持。
- SQL 與 UI 不含帳密、連線字串、原始 provider 例外或資料列；local journal 只含 allowlisted 操作 metadata。
- 僅使用已授權 `(localdb)\mySQLPunkExportTest_20261012`、既有 authorization manifest、C: token-owned fixture；不存取正式庫／已存連線／帳號設定。
- 保留既有 BOM/CRLF；不修改 unrelated files，不執行 ReportExportTests、不調整 AV、不 push/merge/release。
- 使用 `build.bat`／既有 smoke runner；不以 `dotnet build` 取代舊式專案工作流。生成的大型 SQL、journal fixture、logs 保持 local-only。

## Review Focus

1. ON 已生效但回應遺失：不能只靠 acknowledged bool 決定是否清理。Task3 fault seam、Task6 實際 ON 後注入例外驗證。
2. 來源方法提前 Dispose／使用已取消 token：會讓 OFF 無法恢復。Task4 明確 FinishRead，Task6 取消／斷線驗證。
3. 其他作業／DBA／同名重建庫：不能把共用開關當成 job-local。Task3 身分核對和 app lock；Task6 合作與外部交易情境。
4. 本機紀錄毀損／錯誤身分／路徑導向：不得自動執行 OFF、刪掉不屬於本 job 的檔案或洩漏秘密。Task2/5/6。
5. 預設改為否後測試悄悄失去一致性意義，或 UI 把清理算成表完成：Task1 明確舊測試模式、Task4/5 進度回歸、Task6 混合時間 FK 還原反例。

## Files and boundaries

| 責任 | 檔案 |
| --- | --- |
| 新 immutable capability/identity/cleanup models | `mySQLPunk/lib/MssqlExport/MssqlSnapshotModels.cs` |
| 唯讀能力查詢與 SQL settings/app-lock adapter | `mySQLPunk/lib/MssqlExport/MssqlSnapshotBackend.cs` |
| durable 本機紀錄 | `mySQLPunk/lib/MssqlExport/MssqlSnapshotRecoveryStore.cs` |
| 設定生命週期 | `mySQLPunk/lib/MssqlExport/MssqlSnapshotLifecycle.cs` |
| request/catalog/schema/result integration | `MssqlExportModels.cs`, `MssqlExportMetadataReader.cs` |
| 交易、清理、結果與 SQL header | `MssqlExportSource.cs`, `MssqlSqlExportService.cs`, `MssqlSchemaScriptWriter.cs`, `MssqlExportProgressEstimator.cs` |
| UI、狀態與 notice | `mySQLPunk/MssqlExportWizardForm.cs`, `mySQLPunk/Form1.MssqlExport.cs` |
| 新 unit / live cases | `tests/SmokeTests.MssqlExport.Snapshot.cs`, `tests/SmokeTests.MssqlExport.SnapshotIntegration.cs`, `tests/MssqlExportFixtures/snapshot.sql` |
| 測試與編譯入口 | `mySQLPunk/mySQLPunk.csproj`, `tests/SmokeTests.cs`, `tests/Run-SmokeTests.ps1`, `tests/Run-MssqlExportIntegration.ps1` |

## Shared contracts

以下型別均 internal；未列修改的現有 public provider contract 不變。新 constructor 參數放最後，以明確預設值維持既有呼叫編譯。

- `MssqlExportRequest(..., bool useSnapshot = false)` 與唯讀 `UseSnapshot`；SchemaOnly + true 拒絕。Wizard 不記憶上次肯定選項。
- `MssqlSnapshotIdentity(string serverName, string databaseName, int databaseId, DateTime databaseCreateDate)`：比對伺服器原生名稱、實際 DB 名／ID／create_date；create_date 按伺服器返回值比對，不猜時區。僅在當前存活 session 使用此身分授權清理，禁止拿歷史紀錄自動重連修復。
- `MssqlSnapshotCapability(identity, int state, bool? canAlter, bool? isWritable)`，唯讀 `Identity/State/CanAlter/IsWritable`；`CanManage` 僅身分完整、state0或1、兩個能力為true。`UnavailableReason` 使用固定 enum，不能 provider 自由文字。
- Catalog/Schema constructor 加最後的 `MssqlSnapshotCapability snapshotCapability = null`；保留 `SnapshotState`，有 capability 時須與 State 一致，null 表示能力未知。Preflight exposes `SnapshotCapability`；capability/identity 不加入 schema fingerprint。
- `MssqlSnapshotCleanupState { NotRequired, PreservedOn, RestoredOff, Unconfirmed }`；`MssqlSnapshotCleanupResult(State, int? observedState, string recoveryRecordPath, string warningCode)`，全唯讀。
- `MssqlExportResult` 加最後 `bool useSnapshot = false, MssqlSnapshotCleanupResult snapshotCleanup = null`；現有 Stage/PublishedPath/MirrorPath/Warnings 保留。復原問題不增加與每種匯出結果交叉相乘的 terminal enum。
- 在現有 `MssqlExportStage` 尾端加 `EnablingSnapshot, RestoringSnapshot`，保留既有值。
- `IMssqlSnapshotBackend`：`ReadCapability(CancellationToken)`、`TryAcquireLock(CancellationToken): bool`、`SetEnabled(bool, CancellationToken)`、`ReleaseLock(CancellationToken)`、`bool IsUsable`；SQL adapter 只借用既有 SqlConnection，沒有開新連線／Dispose owner 的權利。
- `MssqlSnapshotRecoveryRecord(JobId, Identity, OriginalState, Phase, int? ObservedState, DateTime UpdatedUtc, string NoticeCode)`；Phase/NoticeCode 為固定 enum，版本欄位固定1。
- `IMssqlSnapshotRecoveryStore`：`Create(record): string`、`Update(record)`、`RemoveConfirmed(MssqlSnapshotRecoveryRecord expected)`、`ReadPending(): IReadOnlyList<MssqlSnapshotRecoveryNotice>`。Notice 含 validated record/path 或固定 read-error category；不含原始 exception。
- `MssqlSnapshotLifecycle(backend, store, jobId)`：`Prepare(Action<MssqlExportStage> report, CancellationToken)`；`Restore(Action<MssqlExportStage> report): MssqlSnapshotCleanupResult`；`Release()`。Prepare/Restore 各有整體60/30秒 deadline，SQL CommandTimeout 取剩餘時間，使用 async command + token；不是每次查詢重新給滿期限。
- `IMssqlExportSource.BeginRead(request, Action<MssqlExportStage> report, CancellationToken)`；加 `FinishRead(Action<MssqlExportStage> report): MssqlSnapshotCleanupResult`。CompleteRead 只做最後 schema 複核與 commit；FinishRead 明確關 reader／剩餘交易→恢復設定→放 lock/session。FinishRead 對一般 SQL/IO 清理錯誤回傳安全結果、冪等；Dispose 為最後資源釋放，不隱藏 ALTER。
- `MssqlSqlExportService.LoadRecoveryNotices(MssqlSnapshotIdentity identity): IReadOnlyList<MssqlSnapshotRecoveryNotice>`：純本機、可覆寫的 UI seam；所有 tests 注入 temp store，不讀真實使用者 journal。

下列 Task 的產品檔名縮寫一律位於 `mySQLPunk/lib/MssqlExport/`；Wizard/Routing/Worker/Schema/Metadata/Integration 測試縮寫分別指 `tests/SmokeTests.MssqlExport.<name>.cs`（Metadata 實際位於 `tests/SmokeTests.MssqlExport.cs`）。新 partial tests 必須加入兩個明確註冊入口。

## Task 1: 明確模式、能力模型與唯讀探測

**Files:** 新建 SnapshotModels/Backend；修改 Models/MetadataReader/csproj；新增 Snapshot unit partial 與兩個 test registrations；更新現有 request/schema test factories。
**Consumes:** 已核准 spec；現有 read-only SqlCommand/cancellation owner。
**Produces:** Shared contracts 的 request/capability/identity/result/stages；`MssqlSnapshotBackend.ReadCapability`，此時具體 Backend 類別只提供讀取，不宣告實作完整 IMssqlSnapshotBackend；Task3 完成其他 methods 後才實作該 interface，沒有產品 stub。

- [x] 新增 RED cases `SnapshotOptions_DefaultAndSchemaOnly`：`Assert(!request.UseSnapshot)`；SchemaOnly + true throws；`SnapshotOptions_CapabilityMatrix`：僅0/1 + ALTER=true + writable=true + identity完整可管理；`SnapshotOptions_Fingerprint`：相同 schema 的 capability 改變，`Assert(a.Fingerprint == b.Fingerprint)`。
- [x] 用 `MYSQLPUNK_SMOKE_FILTER='MSSQL export snapshot options'` 執行既有 Release smoke runner，確認因缺少契約或行為失敗；記錄實際 RED。
- [x] 實作 immutable models 與 request 參數。讀取 `sys.databases` 的 state/id/create_date/name、`SERVERPROPERTY('ServerName')`、`HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER')`、`DATABASEPROPERTYEX(DB_NAME(),'Updateability')`；明確 SQL CONVERT 回傳型別，NULL 保留未知。沿用 caller transaction/token，資料庫不存在／取消／斷線仍是真正錯誤。ReadCatalog/ReadSchema 傳遞 capability。
- [x] 舊測試需要 SNAPSHOT 時明確傳 true；SchemaOnly 傳 false。先保留既有 engine validation，Task4 才切換 start/read 的狀態規則。不要透過將所有 fake capability 預設有權限來掩蓋新 unknown case。
- [x] 同 filter GREEN；`build.bat debug`。保存命令與證據，只提交本項確切檔案，獨立覆核模型與權限語意。

## Task 2: 可追查的本機復原紀錄

**Files:** 新建 SnapshotRecoveryStore；修改 csproj、Snapshot unit partial、test registrations。
**Consumes:** Task1 identity/record/notice contracts。
**Produces:** `MssqlSnapshotRecoveryStore(string root = null)` 實作 IMssqlSnapshotRecoveryStore；null 才使用 spec 的 LocalApplicationData 路徑。

- [x] RED `SnapshotRecovery_DurableCreateAndUpdate`：Create 成功後能從另一個 store 讀回精確 job/identity/phase；已存在 job 檔拒絕覆寫；`SnapshotRecovery_UntrustedFiles`：壞 JSON、>64KiB、未知版本、檔名/job不符及 reparse path 不被信任；`SnapshotRecovery_NoSecretsOrUnsafeDelete`：沒有 connectionString/password/providerMessage 欄位，RemoveConfirmed 只刪 matching job 與 expected identity/original-state 的紀錄。
- [x] 執行 filter `MSSQL export snapshot recovery`，取得 RED。
- [x] 實作每 job `<guid-N>.json`、allowlisted DTO、UTF-8、原子同目錄 temp→move/replace。Create/Update 使用 FileStream.Flush(true) 在執行 ON 前完成落地；root/祖先/檔案 reparse 一律拒絕。只讀頂層，單檔64KiB，最多100筆加 overflow notice；不遞迴、不根據 JSON 中的 path 刪除。讀取失敗回固定安全分類；Create/Update 寫入失敗必須明確失敗，不能取得成功 receipt 或繼續 ON；不呼叫 SQL。
- [x] 注入 IO fault 驗證 Create 未完成時 lifecycle 不得取得可用 journal receipt；Update/Delete 失敗保留可追查結果。全部用測試 temp root，保留 BOM/newlines 無關檔案。
- [x] 同 filter GREEN，diff check；僅提交確切檔案，覆核持久性順序、路徑擁有權與機密邊界。

## Task 3: 有界的 SNAPSHOT 設定生命週期

**Files:** 新建 SnapshotLifecycle；完成 SnapshotBackend；修改 csproj、Snapshot unit partial、test registrations。
**Consumes:** Task1 backend/capability；Task2 journal。
**Produces:** Shared contracts 的完整 Prepare/Restore/Release；獨立於 data reader 的 deterministic settings state machine。

- [x] RED `SnapshotLifecycle_PreserveOnAndRestoreOwnedOff`：初始ON `Assert(backend.AlterCalls.Count == 0)`；初始OFF操作順序為 lock→fresh capability→journal flush→ON→confirm1→後續 Restore→OFF→confirm0→remove→unlock。`SnapshotLifecycle_UnknownEnableAndIdentity`：真ON後throw也要回讀／清理；身分改變不得ALTER；`SnapshotLifecycle_CancelDeadlineAndResidual`：已取消 token 仍走獨立清理，OFF失敗回Unconfirmed且journal存在。
- [x] 執行 filter `MSSQL export snapshot lifecycle`，取得 RED。
- [x] SQL adapter 用 resource `mySQLPunk.MssqlExport.SnapshotSetting.v1`、Exclusive、Session、public、LockTimeout=0；negative return code分類，不能當成功。所有 SNAPSHOT jobs（含原本ON）先 lock 後重新讀狀態，普通 jobs 不用此鎖。以既有 Quote() 處理 DB identifier，切換前再次核對存活 session 身分。
- [x] 實作 Prepare：僅state0/1且能力符合政策；state0先寫 durable journal，記錄「已嘗試」再送ON，確認1才交出成功。任何 error 都保留 controller 供明確 Restore；不在 catch 提早 Dispose session。原本ON無 journal、無ALTER。
- [x] 實作 Restore：呼叫者先釋放資料交易；獨立30秒deadline，讀實際狀態，匹配身分且本次原始OFF才可能送OFF。state0確認完成；state1嘗試OFF；transition/unknown/closed不能宣稱成功，有限回讀後保留紀錄。不得在已失去 session lock/連線後 reconnect。日誌更新/刪除與 lock release 失敗也以固定 notice 保留，不能抹去主錯誤。
- [x] 完成 `SnapshotLifecycle_NoJournalNoAlter`、busy同庫、獨立db、NULL能力、withdrawnpermission、repeatedRestore、observerthrows 等矩陣；fake clock/deadline供 unit 使用，產品60/30常數不改短。GREEN、Debug build、精確提交與 lifecycle 覆核。

## Task 4: 串流引擎、明確清理與真實結果

**Files:** 修改 Source/Service/MetadataReader/SchemaScriptWriter/ProgressEstimator；更新 Worker/Schema/Metadata/Integration test wrappers 與 Snapshot unit cases。
**Consumes:** Task1–3 contracts/controller/store。
**Produces:** 一般與 SNAPSHOT 兩條完整來源流程，typed cleanup outcome 傳到結果；本機驗證開始前已釋放 DB/SSH。

- [x] RED `SnapshotWorker_ModeAndCleanupOrder`：ordinary=ReadCommitted、schemaonly無transaction、snapshot=Snapshot；`Assert(events.IndexOf("restore") < events.IndexOf("local-verify"))`；`SnapshotWorker_PrimaryFailureAndRecovery`：失敗／取消 + Unconfirmed 都保留；完整資料 + Unconfirmed 可發布且警告；`SnapshotWorker_ProgressDoesNotCompleteInterruptedTable`：data→restore 不增加 CompletedTables，沒有虛構ETA。
- [x] 執行 filter `MSSQL export snapshot worker`；同時記錄現有 worker/filter 的受影響斷點。
- [x] 拆開 validation：`Validate(schema,request)` 僅物件/必要讀取條件；新增 `ValidateStart(schema,request)` 合併前者及選是時的 capability gate（允許state0/1）。Source Inspect 使用 ValidateStart；writer與最終schema複核只用 Validate，不能於寫DDL時因設定管理權限改變而假裝結構不支援。BeginRead 的 Prepare 完成後另外確實檢查 state1，才建 SNAPSHOT transaction。
- [x] Source 依UseSnapshot建立明確交易；OpenRows維持既有持續reader與bounded LOB。改掉 BeginRead/OpenRows/CompleteRead 中會先丟失session的 Dispose 路徑；Service 在 nested finally 一定呼叫 FinishRead，再 Dispose。清理使用不檢查作業取消的 report callback，不能重用現有會 ThrowIfCancellationRequested 的 phase callback。交易無法確認結束則釋放owner、留下Unconfirmed，不能在活躍/未知交易上送ALTER。FinishRead正常SQL/IO錯誤回安全結果，不用 canceled token，也不覆寫 primary error。
- [x] SQL header 在 DataOnly early-return之前寫明模式；ordinary result加入 spec 警語，維持可信任FK WITH CHECK。補 typed result設定復原結果與journal殘留路徑；單純警語不改 PublishedPath/Stage 的檔案真實性。
- [x] 新階段採 unknown ETA／等待顯示。ProgressEstimator 的 table change 不能在 RestoringSnapshot 將中斷表算完成；完成來源階段仍能正確統計最後一表。注入callback failure、file verify/publish/mirror failure覆核清理只做一次。
- [x] 新filter及既有 `MSSQL export worker`、`schema`、`metadata`、`lob`、`values` GREEN；Debug/Release串行build；精確提交後覆核資源所有權與結果優先序。

## Task 5: 精靈選項、恢復提示與主視窗狀態

**Files:** 修改 Wizard/Form1.MssqlExport；Service加local-notice seam；更新 Wizard/Routing smoke cases與fixture preview。
**Consumes:** Catalog/Preflight capability、request.UseSnapshot、result.SnapshotCleanup、Task2 pending notices、Task4 progress。
**Produces:** 原生可操作是/否選項與完整中文/英文提示；舊三步選擇不被重載清空。

- [x] RED `SnapshotWizard_DefaultDisableAndRadioGroups`：初始否、無ALTER/NULL/轉換中/SchemaOnly停用；snapshot radios獨立容器，不改mode radio選取。`SnapshotWizard_RecheckAndGeneration`：能力下降清楚改否並清除prepared，不能沿用Yes確認直接開跑；過期結果不恢復按鈕。`SnapshotWizard_RecoveryAndTerminalStatus`：主窗與結果均顯示Unconfirmed，cancel時仍看得到RestoringSnapshot。
- [x] 執行新filter `MSSQL export snapshot wizard` 取得RED。
- [x] BuildOutput加入具名radio panel `mssqlSnapshotOptions`、`mssqlSnapshotYes/No`、`mssqlSnapshotReason`、`mssqlSnapshotWarning`、`mssqlRecoveryNotices`。缺能力不令catalog loaded=false；保留SELECT/definition錯誤。設定頁可重新檢查能力；以現有generation/cancellation保護非同步更新。
- [x] PreflightAsync捕獲immutable選項，移除一律state1門檻，按選項顯示ordinary警語或temporary-setting說明。確認／SQL／結果的mode一致。新表單預設否，SchemaOnly強制否，切回資料模式也不擅自恢復是。
- [x] LoadRecoveryNotices純本機讀取，依已讀原生identity關聯；journal無法讀取/過量時有安全提示。展示已知狀態/時間/path與人工查詢方式，不執行OFF、不自動刪歷史紀錄。UI測試inject temp store／fake notices。
- [x] RenderProgress允許cancelRequested時呈現RestoringSnapshot；titlebar關閉仍等待明確清理。BuildMssqlExportStatus針對Completed+Unconfirmed顯示帶警告完成，Failed/Canceled/MirrorIncomplete保持原始狀態並附復原警告。radio/readout在640×480與既有縮放fixture可達。
- [x] 新filter及既有wizard/diagnostic/routing GREEN；原生fixture深淺色驗收（不是HTML），保留Punky與mute行為；精確提交與UI/結果覆核。工具不可用時明確記pending，不假造截圖。

## Task 6: 隔離實際驗收與交付

**Files:** 新建 SnapshotIntegration partial、snapshot.sql；修改 Integration/CompatibilityIntegration、兩個runner與registrations；更新 docs/MSSQL_SQL_EXPORT.md、design.md、production-preflight.md、verification.md，追加 ignored history。
**Consumes:** 完整產品Task1–5；原有token-owned fixture/authorization/classic sqlcmd。
**Produces:** OptionalSnapshot/SnapshotLifecycle實際證據及新模式回歸，沒有正式機成功推論。

- [x] 為profile加入RED驗收：`OptionalSnapshot`、`SnapshotLifecycle` 必須明確dispatch；fixture建庫初始ON/OFF可選，不能總是先ON。Request helper的既有data案例明確Snapshot=true，SchemaOnly=false；新ordinary案例false。注入的journal root限fixture內。
- [x] OptionalSnapshot：ON/OFF普通模式＋WITHOUT LOGIN無ALTER帳號實際匯出還原；原state不變且command audit無ALTER；必要讀取權限拒絕不退化成成功。明確Parent-before-Child次序，Parent讀完後由第二連線合法新增Parent2、把Child改指向2；ordinary還原必須可信任FK失敗，Snapshot同一操作還原成功。另驗RC取消／schema drift／舊檔保護。
- [x] SnapshotLifecycle：OFF→ON→OFF完成/取消/讀取失敗；初始ON皆保持ON。持有已授權測試writer造成ON等待，觀察transition後放行/取消/一次真實60秒timeout；外部owned SNAPSHOT實際讀取後持有，觀察OFF等待30秒與Unconfirmed。另驗同庫busy、以已owned Target作第二來源的跨庫獨立、ALTER撤銷、journal IO失敗、真ON後注入ack例外、只關閉exporter自身SqlConnection造成斷線；後兩者分別標記fault injection，不冒稱真實網路故障。
- [x] 程序中斷案例：由parent fixture以既有SmokeTests.exe窄範圍child模式啟動工作，不另建fixture；child驗證server/token/root/database白名單，durable journal及ON後送ready。child以hidden window／CreateNoWindow啟動；parent僅終止自己記錄的child PID，觀察DBstate／journal與下次read-only notice。測試owner人工回復資料庫屬fixture teardown，與產品自動清理證據分開。
- [x] 修改已過時斷言：Core的OFF一律拒絕改成新capability/模式；Properties的無ALTER + ON成功案例明確ordinary，另加選Snapshot遭拒；Wizard state gate也更新。不得刪除真正permissions／unsupported gates來湊PASS。
- [x] 執行以下live profiles，依每次exitcode停止並保留失敗證據；每個generated DB需記錄清理。每個還原保存實際engine/client、artifact bytes/hash、source/target比較、snapshot最終state、journal及實際restoreexit。

```powershell
$fixture = Get-Content -LiteralPath '.superpowers/sdd/implementation-plan/fixture-environment.json' -Raw | ConvertFrom-Json
$sqlcmd = 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE'
foreach ($profile in @('OptionalSnapshot','SnapshotLifecycle','Core','Concurrency','Properties','References','Geometry')) {
    $out = Join-Path $fixture.FixtureRoot ($profile + '-' + [guid]::NewGuid().ToString('N'))
    powershell.exe -NoProfile -File tests/Run-MssqlExportIntegration.ps1 -Run -Server '(localdb)\mySQLPunkExportTest_20261012' -Profile $profile -Client Classic -SqlcmdPath $sqlcmd -OutputDirectory $out
    if ($LASTEXITCODE -ne 0) { throw "$profile failed; preserve evidence" }
}
```

Runner保留原本server/token/path/reparse/20GiB gates。需要時依既有授權啟動該專用instance；不更改runner自動啟動policy，不使用正式連線。已授權test teardown的generatedDB強制清理與產品禁止強制OFF分開。

- [ ] 清除本task測試環境變數後跑完整Release smoke及AutoReleasePolicy，再串行Debug/Release build。指令見下方；AV攔截就報pending，不改名重跑或變更防毒。獨立全分支review聚焦DB狀態殘留、混合時間資料／FK、權限與檔案真實性；無新material finding即停止review循環。
- [x] 更新使用說明與舊spec的被取代段落，保留歷史證據原日期。SQL2025 LocalDB與正式SQL2019、模擬與真實失敗、nativefixture與真人長作業分開。先前>2GiB未重跑不得稱本版已重驗；此改動不改buffer/codec尺寸，Geometry/Core回歸覆蓋新的來源生命週期。精確提交程式/測試/文件，保留worktree供使用者試跑，不自行merge/push。

本機七 profile、完整 Release smoke/AutoReleasePolicy、串行 Debug/Release 與原生 fixture 已通過；上項仍未勾選是因獨立 Task6/全分支 review 待 controller 完成。

## Commands and completion gate

每項RED/GREEN使用現有runner；新的group前綴按各Task命名註冊，filter是字串比對。獨立測試process不承襲live profile。

```powershell
$env:MYSQLPUNK_MSSQL_INTEGRATION = $null
$env:MYSQLPUNK_MSSQL_WIZARD_PREVIEW = $null
$env:MYSQLPUNK_SMOKE_FILTER = 'MSSQL export snapshot options' # 依Task改成其明確filter
powershell.exe -NoProfile -File tests/Run-SmokeTests.ps1 -Configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Inspect RED or failed regression evidence' }
# 最終全套（不能沿用上述filter）
$env:MYSQLPUNK_SMOKE_FILTER = $null
powershell.exe -NoProfile -File tests/Run-SmokeTests.ps1 -Configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Full smoke failed' }
.\build.bat debug
if ($LASTEXITCODE -ne 0) { throw 'Debug build failed' }
.\build.bat release
if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
git diff --check
if ($LASTEXITCODE -ne 0) { throw 'Whitespace check failed' }
```

完成需同時有：mode/capability與cleanup單元證據、受控寫入下兩種模式的實際還原差異、有限等待／中斷復原證據、原生UI或明確pending、最終build/smoke、覆核結論與history。所有測試結果在執行後填入verification，不提前填PASS。

## Plan self-review

已核對spec各段→Task1–6覆蓋；新型別與介面有單一擁有者；設定管理與物件驗證分開；cleanup在source dispose之前；journal未知結果、progress與FK反例都有明確測試。shared files/build目錄由Task順序控制，禁止同時改相同檔案或共用bin編譯。此段為計畫簽核時的 self-review；後續實作與隔離 DB 驗證見 verification.md，正式設定未修改。
