# MSSQL 大量資料 SQL 匯出精靈 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 將 MSSQL 互動式本機 `.sql` 匯出接到專用精靈，以有界記憶體依序匯出一致快照，顯示進度／ETA／Punky，安全取消與發布，並以實際還原驗證資料。

**Architecture:** 新增 MSSQL 專用 metadata、SQL writer、worker 與 WinForms 精靈；不重寫其他 provider 的 dump API。worker 擁有獨立 SQL connection／SSH owner，以同一 SNAPSHOT 交易讀取選取表；小列 INSERT、大列在還原 session 的私有暫存表分段組回，再插入目標。檔案串流驗證、原子發布、鏡像在來源交易釋放後執行。

**Tech Stack:** 現有 .NET Framework 4.7.2／WinForms／System.Data.SqlClient／MSBuild；既有 ThemeManager、UiKit、PunkyReportLoadingControl、smoke harness；Microsoft sqlcmd 作還原驗收。第一版不增加 NuGet 依賴或換資料庫 driver。

**Spec:** [design.md](design.md)，2026-10-12 修訂版；使用者已回覆「照修訂版進入實作計畫（建議）」。

**Status:** 使用者於 2026-10-12 核准計畫並選擇「分工實作並逐項覆核」；實作已開始，checkbox 依驗證進度更新。畫面原型及規格核准不表示 SQL 還原、效能或正式程式已完成。

## Global Constraints

- 範圍是 MSSQL 互動式本機 SQL 邏輯匯出；原生 `.bak`、排程／自動備份改造、其他 provider 重構、暫停／續傳、壓縮／加密均不納入。
- 持續寫入來源採同一個 SNAPSHOT 交易；不自動變更 DB 設定。`snapshot_isolation_state` 必須是 ON，僅結構模式例外；不降級為 NOLOCK／一般讀取。
- 每表只開一個 SequentialAccess reader；不得整表 DataTable、OFFSET 分頁、先 COUNT 全表、完整 GetValue/GetString 大欄位或完整 SQL StringBuilder。
- 整列的小資料緩衝預算最多 256 KiB；binary chunk 至多 64 KiB，文字起始 chunk 至多 16K 個 UTF-16 碼單元；一般資料 statement 生成預算 256 KiB，GO batch 目標不超過 4 MiB。計入實際編碼／hex／轉義 bytes；這些不是單列拒絕上限。
- 跨 GO 組裝需要同一匯入 session；先完整組裝及驗證，再一次 INSERT 到目標。`sqlcmd -i`、`-b`、`-x`、明確 UTF-8 輸入設定為驗收契約。
- 原生 geometry／geography、UDT 等 spec 所列特殊能力預檢阻擋受影響選取；一般文字中的 WKT 不解析、縮短或簡化。
- 進度一般更新約 100–250ms；完成前最多 99%；未知 ETA 顯示「估算中」，不是 0 秒。鏡像未完成不可顯示整體成功。
- 取消等 worker 清理；失敗不先刪原正式檔。鏡像驗證／發布後才清理保留份數，沿用原有分類規則。
- Punky 使用現有素材及 BeginExport／EndExport／Muted；自動 loop、原音一次、只有靜音按鈕，不增加播放／暫停。
- 保留既有 BOM／換行及無關工作，尤其 `mySQLPunk.Desktop/Properties/`；不以 `git add -A` 收檔。重要決策與證據追加既有 `history.md`。
- 不還原／執行／重命名或重 host `ReportExportTests.exe`，不搬其測試到其他 runner，也不更改防毒。新 MSSQL 測試若受阻，停止並列 pending。
- 本計畫的建庫、還原與大量資料測試只能在明確隔離 fixture 執行；不使用使用者正式 DB／已儲存帳密。沒有 fixture 就保留 DB 驗收 pending，不能用編譯代替。

## Review Focus

以下五類容易落在主流程外，已各自納入後面測試步驟：

1. 資料包含換行後的 `GO`、`:r`、`$(name)`、NUL／非 BMP 字元；匯入不能把資料當作工具命令或變數，值須完整還原（Task 4、5、10）。
2. 兩個 schema 有同名表，或預檢後 drop/recreate；不能依顯示字串選錯表，也不能把新物件當原快照（Task 2、7、8）。
3. 目標檔在覆寫同意後改變，或取消恰好遇到發布／鏡像；不得刪掉另一工作的檔案或把已發布結果說成未輸出（Task 6、7、10）。
4. SQL Server 的 metadata 欄位因版本不同而不存在、權限不足、view 定義不可見；須區分不支援與讀取失敗，不能當成「沒有資料／沒有約束」（Task 2、3、10）。
5. 無 PK、多個大欄位、來源空值、目標 tempdb 定序不同，或還原 client 在錯誤後仍繼續；不得插入半列、改變 bytes 或重複最後 INSERT（Task 4、5、10）。

## 檔案與接點

以下為計畫中的新檔，尚不存在。新引擎放 `mySQLPunk/lib/MssqlExport/`，namespace `mySQLPunk.lib.MssqlExport`，型別預設 internal；避免擴大公用 IDatabase contract。

| 檔案 | 責任／擁有者 |
| --- | --- |
| `MssqlExportModels.cs` | 不可變 request、metadata、progress、結果、限制值及來源測試介面 |
| `MssqlOwnedSession.cs` | owner 與原生 SqlConnection 成對持有，單一 Dispose |
| `MssqlExportMetadataReader.cs` | sys metadata、能力偵測、預檢及 schema fingerprint |
| `MssqlExportSource.cs` | 快照交易、command／reader／取消登記、來源生命週期 |
| `MssqlSchemaScriptWriter.cs` | 原生 DDL、依賴排序、index／constraint／view／comment |
| `MssqlDataScriptWriter.cs` | 逐列／逐欄 reader、有限小列緩衝與分段切換 |
| `MssqlValueCodec.cs` | 型別保真、identifier／scalar／資料 literal 編碼 |
| `MssqlLobScriptWriter.cs` | 大列 staging、chunk、完整性 guard 與最後 INSERT |
| `MssqlExportArtifact.cs` | 工作暫存、incremental hash、重讀驗證、發布／清理 |
| `MssqlExportMirror.cs` | 分段鏡像、驗證後發布及既有 retention |
| `MssqlExportProgressEstimator.cs` | 計量、節流、平滑 ETA／未知狀態 |
| `MssqlSqlExportService.cs` | 階段 orchestration、錯誤／取消與完整結果 |
| `mySQLPunk/MssqlExportWizardForm.cs` | 專用 WinForms 精靈，沿用樣式與 Punky |
| `mySQLPunk/Form1.MssqlExport.cs` | 既有四個互動入口共用的設定捕獲及開窗接點 |
| `tests/SmokeTests.MssqlExport.cs` | 新 MSSQL 純單元、fake-reader、檔案與 UI 狀態測試 |
| `tests/SmokeTests.MssqlExport.Integration.cs` | 明確啟用的隔離 DB／restore／stress 測試，不預設跑 |
| `tests/Run-MssqlExportIntegration.ps1` | 指定測試 server、profile、全新輸出目錄，保存證據 |
| `docs/MSSQL_SQL_EXPORT.md` | 實際支援範圍、操作、還原命令、資源與錯誤處理 |

確定需要的既有修改：`lib/ConnectionOpenService.cs`、`Form1.cs`、`mySQLPunk.csproj`、`Properties/AssemblyInfo.cs`、`tests/SmokeTests.cs`、`tests/Run-SmokeTests.ps1`。`AssemblyInfo` 只新增測試 friend assembly，不改版本。沿用 `BackupRemoteMirrorService.PruneRemoteBackups`，不呼叫會提早 prune 的 `MirrorBackup`，不必改既有其他 provider 的方法。

不需要修改 `my_mssql.cs`、`ConnectionSecurityService.cs`、`IDatabase`、`DatabaseDumpService`、`BackupIntegrityService`、既有報表 writer／動畫素材或 `.bak` 表單。若後續證據要求修改，先說明必要性，不順手重構。

### 現有入口（2026-10-12 唯讀定位）

- `Form1.cs:6634` `DumpSelectedTableSql(bool dataOnly)`：單表右鍵，另有自己的同步 dump。
- `Form1.cs:6902` `DumpCurrentSelectionSqlWithDialog(bool dataOnlyForTable)`：工具列／群組；view selection 不可誤轉全庫。
- `Form1.cs:7258` `DumpSelectedDatabaseSqlWithDialog()`：資料庫結構與資料。
- `Form1.cs:8040` `BackupSelectedDatabaseWithDialog()`：建立備份；此入口捕獲 `BackupMirrorSettings.RemoteDirectory/RetainCount`。
- `CreateDatabaseBackup` 還被還原前／刪除前備份等流程呼叫，本版不全面替換它。只在上述互動 handler 的 MSSQL 分支轉精靈，其他 caller 保持原流程並在操作文件註明範圍。
- `ConnectionOpenService.Open(Dictionary<string,object>, false)` 已持有 raw provider 與可能的 SSH wrapper；在這個邊界傳遞原生連線，不 unwrap private 欄位或另開 connection string。

## 共用契約與測試方式

### 跨 task 的型別

全部在 Models 檔定義，collection 在建構時複製成唯讀；不得讓 UI 在開始後改動 request。

- `MssqlObjectKey`：`int ObjectId`、`string Schema`、`string Name`；metadata identity 以 object_id 加原始 schema/name 為準，SQL 引用各段名稱。
- `MssqlExportMode`：`SchemaAndData, SchemaOnly, DataOnly`。
- `MssqlMirrorOptions`：`string Directory`、`int RetainCount`；null 表示未啟用。
- `ExportDestinationStamp`：目標是否存在、正規路徑、檔案 identity／長度／最後寫入時間及 streaming SHA-256；在確認覆寫時捕獲，在發布前重新比對。不將 UI 同意當永久覆寫權。
- `MssqlExportRequest(Guid jobId, string databaseName, IReadOnlyList<MssqlObjectKey> tables, MssqlExportMode mode, bool includeViews, string targetPath, ExportDestinationStamp destination, MssqlMirrorOptions mirror)`；驗證非空選取、路徑、data-only 無 views。
- `MssqlTableDefinition`／`MssqlColumnDefinition`／`MssqlIndexDefinition`／`MssqlConstraintDefinition`／`MssqlViewDefinition`：Task 2 依 spec 定義 native metadata；禁止沿用跨庫映射字串。Column 必有 ordinal、native type、length、precision/scale、collation、nullable、identity、computed、rowversion、comment；Table 含 key、comment、nullable estimated rows/bytes。
- `MssqlExportCatalog`：tables、snapshot state、readability／capability issues；`MssqlExportIssue`：code、object key、optional column、可呈現原因。
- `MssqlExportPreflight`：本次選取的schema摘要、snapshot state、`IReadOnlyList<MssqlExportIssue> Issues`、`bool CanStart`。確認頁使用無長交易的Inspect；開始後必須重查，不能把UI預檢當成凍結來源。
- `MssqlExportSchema`：凍結的 tables/views/dependencies、canonical definition fingerprint；`MssqlTableRowCount`：object key、`long Rows`。
- `MssqlExportStage`：Preflight, Schema, Data, Verifying, Publishing, Mirroring, CancelRequested, Completed, Canceled, Failed, LocalCompletedMirrorIncomplete。
- `MssqlExportProgress`：JobId、Stage、CurrentTable、long 實際 rows/bytes、nullable estimates、完成表數、elapsed、nullable ETA、nullable percent、估計狀態、限長訊息；immutable snapshot。
- `MssqlExportResult`：Stage、PublishedPath、MirrorPath、row counts、elapsed、Warnings、ResidualPaths；只有已發布才有 PublishedPath。
- `MssqlExportLimits`：`SmallRowBytes=262144`, `StatementBytes=262144`, `BatchBytes=4194304`, `BinaryChunkBytes=65536`, `TextChunkCodeUnits=16384`；限制算法的值，不用作資料拒絕上限。

### 測試命令

實作新增測試沿用 `SmokeTests` 的 `Assert`、`AssertEquals`、`AssertThrows<T>`；case 名稱有 `MSSQL export <group>` 前綴。增加 `InternalsVisibleTo("mySQLPunk.SmokeTests")`，不把整個新引擎改為 public。每 task 使用以下命令，將 group 替換為其指定值，並保存原環境變數：

```powershell
$savedMssqlSmokeFilter = $env:MYSQLPUNK_SMOKE_FILTER
try {
    $env:MYSQLPUNK_SMOKE_FILTER = 'MSSQL export session'
    .\tests\Run-SmokeTests.ps1 -Configuration Debug
} finally {
    $env:MYSQLPUNK_SMOKE_FILTER = $savedMssqlSmokeFilter
}
```

紅燈必須是新 assertion／未實作介面造成，不把 NuGet／compiler／AV 失敗當成正確紅燈；綠燈為匹配 case 全 PASS、runner exit 0，並保留其既有 AutoReleasePolicy 檢查。新增 integration cases 必須在 `MYSQLPUNK_MSSQL_EXPORT_LIVE=1` 且 runner 驗證 fixture 時才註冊，預設 smoke 不連 DB、不產生大檔。

所有檔案測試只在以 CreateNew／GUID 建立的專屬新目錄內模擬舊檔；不碰使用者現有輸出。新 runner 只測 MSSQL 新功能，不參照 ReportExportTests source。測試支援物件放該 partial：可控 clock、可注入錯誤的 stream、拒絕 GetValue/GetString／逆序 ordinal 的 DbDataReader、記錄呼叫次序的 fake source。

## Task 1：獨立連線 owner 與不可變 request

**Files:** Create `lib/MssqlExport/MssqlExportModels.cs`, `lib/MssqlExport/MssqlOwnedSession.cs`, `tests/SmokeTests.MssqlExport.cs`; Modify `lib/ConnectionOpenService.cs`, `Properties/AssemblyInfo.cs`, `mySQLPunk.csproj`, `tests/SmokeTests.cs`, `tests/Run-SmokeTests.ps1`。產品路徑皆在 `mySQLPunk/` 下。

**Interfaces:**

```csharp
// ConnectionOpenService：既有 public overloads 保留。
internal static MssqlOwnedSession OpenMssqlSession(Dictionary<string, object> connection);
// MssqlOwnedSession : IDisposable
internal MssqlOwnedSession(IDatabase owner, SqlConnection connection);
internal SqlConnection Connection { get; }
public void Dispose();
```

- [x] **1. 寫失敗測試，group `session`。** `Session_DisposesOwnerOnce` 以 fake IDatabase 計次；`Request_CopiesSelectionAndRejectsInvalidMode` 修改輸入 List 後 request 不變；測試 core assertions：
  ```csharp
  session.Dispose(); session.Dispose();
  Assert(owner.DisposeCount == 1, "SQL provider and SSH lease have one owner");
  Assert(request.Tables.Count == 1, "Request must not retain mutable selection");
  Assert(MssqlExportLimits.SmallRowBytes == 262144, "Row buffer budget");
  ```
- [x] **2. 跑 session group，確認紅燈。** 編譯／註冊 wiring 與 friend assembly 併入這個可測 deliverable，不另做空 scaffolding task。
- [x] **3. 實作上述介面與 Models。** `ConnectionOpenResult` 加 internal native SqlConnection property／constructor，在既有 raw provider 建立點傳入；public constructor 保留。factory 使用 `Open(settings,false)`，驗證 MSSQL；失敗 dispose owner。session 不獨立 Dispose 同一 SqlConnection，command／transaction 留來源層管理。不得使用 ConnectionString 另開第二條連線。
- [x] **4. 跑 session group 為綠，確認既有 connection tests 仍過。** 只用 fake owner／未開啟連線完成生命週期單元測試；真 SSH 另列 Task 10。
- [x] **5. 檢查 diff 與提交。** 僅 stage 本 task 明確檔案；建議訊息 `feat(mssql-export): own export session and immutable requests`。不 push。

## Task 2：原生 metadata、預檢與快照來源

**Files:** Create `lib/MssqlExport/MssqlExportMetadataReader.cs`, `lib/MssqlExport/MssqlExportSource.cs`; Extend Models、task 測試、csproj。

**Interfaces:**

```csharp
internal interface IMssqlExportSource : IDisposable {
    MssqlExportCatalog ReadCatalog(string databaseName, CancellationToken cancellation);
    MssqlExportPreflight Inspect(MssqlExportRequest request, CancellationToken cancellation);
    MssqlExportSchema BeginRead(MssqlExportRequest request, CancellationToken cancellation);
    MssqlRowReader OpenRows(MssqlTableDefinition table, CancellationToken cancellation);
    void CompleteRead(MssqlExportSchema schema, CancellationToken cancellation);
}
// MssqlRowReader : IDisposable owns Reader + command + cancel registration.
internal MssqlRowReader(DbDataReader reader, IDisposable commandOwner);
internal DbDataReader Reader { get; }
// MssqlExportSource : IMssqlExportSource
internal MssqlExportSource(MssqlOwnedSession session);
// MssqlExportMetadataReader
internal MssqlExportCatalog ReadCatalog(SqlConnection connection, string databaseName, CancellationToken cancellation);
internal MssqlExportSchema ReadSchema(SqlConnection connection, SqlTransaction transaction, MssqlExportRequest request, CancellationToken cancellation);
internal IReadOnlyList<MssqlExportIssue> Validate(MssqlExportSchema schema, MssqlExportRequest request);
```

- [ ] **1. 寫失敗測試，group `metadata`。** 覆蓋同名不同 schema／名稱含 `]`、spatial 明列、依賴缺漏、snapshot OFF／過渡狀態、estimate 缺失不是 0、typed XML／UDT 拒絕；assert：
  ```csharp
  Assert(catalog.Tables.Count == 2, "dbo.orders and sales.orders are distinct");
  Assert(catalog.Tables[0].EstimatedRows == null, "Unavailable estimate is unknown");
  Assert(issues.Any(x => x.Code == "UnsupportedSpatial" && x.ColumnName == "shape"), "Name rejected column");
  ```
- [ ] **2. 跑 metadata group 為紅。** metadata mapping 以 fixture rows 測純轉換；SQL查詢本身的證據留 Task 10，不能把 fake 當 live query 驗證。
- [ ] **3. 實作版本／能力感知 sys queries。** metadata 需涵蓋 tables/columns/types/schemas、extended properties、identity/computed/default、rowstore indexes/index_columns、FK/check、view definition/SET/dependencies。用可用 catalog 欄位／版本選查詢，舊版本缺少 ledger／temporal 欄位時不查不存在欄位。看不到 definition 或權限錯誤明確報錯，不以空集合替代。針對 spec 特殊表／型別／索引完整列出阻擋原因。
- [ ] **4. 實作 Source transaction／reader。** 改變 owned connection 的 database；Inspect以無transaction的ReadSchema供確認頁預檢，完畢dispose source，不在使用者思考時持有快照。含資料開始時再確認 snapshot ON，BeginRead 開一個 Snapshot transaction 並重新凍結／驗證 schema；SchemaOnly 不開資料快照。OpenRows 明確欄位、無排序／OFFSET需求、SequentialAccess；command timeout 直接設為 0 並登記 Cancel，dispose 登記先於 command。CompleteRead 核對 object IDs／完整 definition fingerprint，再結束交易；例外／取消 rollback 並 dispose。schema改變中途不得 retry 續跑。
- [ ] **5. 跑 metadata group 為綠並提交。** 加 reader/command/dispose 次序 fake 驗證；實際 snapshot／DDL race 在 Task 10。建議訊息 `feat(mssql-export): collect native metadata and preflight snapshot export`。

## Task 3：原生結構腳本與依賴順序

**Files:** Create `lib/MssqlExport/MssqlSchemaScriptWriter.cs`; Extend Models／測試／csproj。

**Interfaces:**

```csharp
internal void WriteBeforeData(MssqlExportSchema schema, MssqlExportRequest request, TextWriter output, CancellationToken cancellation);
internal void WriteAfterData(MssqlExportSchema schema, MssqlExportRequest request, TextWriter output, CancellationToken cancellation);
```

- [ ] **1. 寫失敗測試，group `schema`。** metadata fixture 含 native varchar／decimal(38,10)／datetimeoffset(7)、identity、computed persisted、rowversion、複合降冪索引／INCLUDE／filter、FK/CHECK trust 狀態、comment、多層 views。assert：
  ```csharp
  AssertContains(sql, "decimal(38,10)", "Keep native precision");
  AssertNotContains(sql, "USE [source_db]", "Restore target is chosen by caller");
  Assert(sql.IndexOf("CREATE TABLE") < sql.IndexOf("FOREIGN KEY"), "Create referenced tables first");
  AssertContains(sql, "GO", "View DDL has its own batch");
  ```
- [ ] **2. 跑 schema group 為紅。** 加 circular FK、未包含 view/function/sequence／trigger、不可見 definition 的失敗案例；不能生成成功卻遺漏物件。
- [ ] **3. 實作前後兩階段 writer。** 前段 schema/table、原生欄位與 default/computed；後段 keys/indexes、FK/CHECK、comments、依賴排序 views。自參照／循环 FK 留資料後建立。資料來源不含 computed／rowversion 值，摘要說明 rowversion 重生。僅資料兩段皆不建立物件；僅結構仍產生完整兩段。型別／identifier 不經 DatabaseDumpService mapper。
- [ ] **4. 明定 restore 語意與 identity 處理。** 結構模式預期空白／無衝突目標，存在物件應明確失敗，不產生 DROP DATABASE/TABLE；DataOnly 要求相容既有表。identity 的 seed/increment/last value 與空表、負 increment 情況須能供 Task 4 資料段及 Task 10 後續新增驗證，避免「已插最大值」被誤當全部規則。
- [ ] **5. 跑 schema group 為綠並提交。** SQL tokens 測試不是還原證據；native execution 留 Task 10。建議訊息 `feat(mssql-export): script native schema and ordered dependencies`。

## Task 4：一般列串流、型別及批次預算

**Files:** Create `lib/MssqlExport/MssqlDataScriptWriter.cs`, `lib/MssqlExport/MssqlValueCodec.cs`; Extend source projection、測試、csproj。

**Interfaces:**

```csharp
internal static string QuoteIdentifier(string identifier); // MssqlValueCodec
internal static string FormatScalar(object value, MssqlColumnDefinition column);
// Writer receives the already-open reader; does not query source again.
internal long WriteTable(DbDataReader reader, MssqlTableDefinition table, TextWriter output,
    Action<long, long> onRowsAndSourceBytes, CancellationToken cancellation);
```

- [ ] **1. 寫失敗測試，group `values`。** decimal 38 不走 CLR decimal、offset與7位小數、GUID、money、float roundtrip、NULL/empty、Unicode／換行／NUL／`GO`／`$(name)`。以 `SqlDecimal` fixture、InvariantCulture、受限 DbDataReader 測：
  ```csharp
  AssertEquals("[a]]b]", MssqlValueCodec.QuoteIdentifier("a]b"), "Quote each identifier");
  AssertContains(decimalSql, "1234567890123456789012345678.1234567890", "Keep all 38 digits");
  Assert(maxObservedStatementBytes <= 262144, "Bound statement bytes");
  Assert(maxObservedBatchBytes <= 4194304, "Bound data batch bytes");
  ```
- [ ] **2. 跑 values group 為紅。** fake reader 對大值 GetString/GetValue、回頭讀 ordinal 立即 throw；偵測 row buffer 最大占用。不只測輸出相等，也測讀取方式。
- [ ] **3. 實作 scalar codec 與串流 projection。** LOB 用 GetBytes／GetChars 小段；對 varchar/text 投影原始 varbinary 保留 code-page bytes，回復時指定來源 collation；Unicode 使用不丟失 UTF-16 code units 的編碼。文字／binary資料 literal使用typed hex expression，避免行首 GO／sqlcmd directive／控制字元穿透；text chunk 不切 surrogate pair。decimal 使用 SqlDecimal；不依當前文化格式化日期／數字。untyped XML 需串流投影及明確 XML 轉型／還原驗證，不能不小心 GetValue 整欄。
- [ ] **4. 實作有限小列 INSERT 與計量。** 使用 source metadata 排除 computed／rowversion，identity 適當 ON/OFF。整列緩衝／encoded statement 接近預算時移交 Task 5，不回查來源。Task 5 完成前大列入口只能明確標為尚未實作並保持 UI 未接線，不能把暫時行為當正式支援上限。空表產生零筆INSERT；若無可插入欄位，以每來源列一個常數投影讀取並逐列INSERT DEFAULT VALUES，不把rowversion當持久值。
- [ ] **5. 跑 values group 為綠，完成小資料 reader cases 後提交。** 建議訊息 `feat(mssql-export): stream typed row values within batch budgets`。

## Task 5：大欄位 staging、分段與失敗保護

**Files:** Create `lib/MssqlExport/MssqlLobScriptWriter.cs`; Extend Task 4 writer、測試、csproj。

**Interfaces:**

```csharp
internal void BeginRow(Guid jobId, long rowOrdinal, MssqlTableDefinition table, TextWriter output);
internal void WriteScalar(int columnOrdinal, string scalarExpression, TextWriter output);
internal void WriteChunk(int columnOrdinal, long chunkIndex, byte[] encodedBytes, int count, TextWriter output);
internal void CompleteColumn(int columnOrdinal, bool isNull, long chunkCount, long expectedBytes, TextWriter output);
internal void CommitRow(TextWriter output);
```

`encodedBytes` 是 codec 指定的原生 binary／varchar bytes／UTF-16 bytes；chunk writer 不保留呼叫者 buffer。型別、collation、欄位列表由 BeginRow 固定。小型非LOB值透過codec生成的scalarExpression交WriteScalar，一個受guard的batch設定typed欄位並標記完成；不能接受UI／外部任意SQL。先前small-row buffer內的scalar與文字／binary分別移交對應方法，無第二次來源讀取。CompleteColumn用於分段欄位，核對非NULL值的chunk／bytes並標記完成；NULL狀態與空值保持區別。

- [ ] **1. 寫失敗測試，group `lob`。** 單元用惰性 reader 供 64 MiB binary、超過 16 MiB Unicode 與多欄位無 PK；不在 fixture 自己先配一整塊大字串。assert：
  ```csharp
  Assert(maxRawBinaryChunk <= 65536, "Bound raw binary chunk");
  Assert(maxTextCodeUnits <= 16384, "Bound UTF-16 chunk");
  Assert(maxSmallRowBuffer <= 262144, "Switch instead of growing row buffer");
  Assert(finalTargetInserts == 1, "Only insert the complete row");
  Assert(sourceReadPasses == 1, "No PK or row reread required");
  ```
- [ ] **2. 跑 lob group 為紅。** 測 NULL 與空值、尾空白、surrogate boundary、bytes 膨脹、small→large切換、text/ntext/image、untyped XML、cancel 中途。測試生成 guard 結構；SQL engine 實際拒絕缺段由 Task 10 驗證。
- [ ] **3. 實作同 session 私有 staging protocol。** 使用工作專屬 `#temp` 名稱；每列紀錄 job/row/column/chunk index/received bytes/expected totals/null/completed/failed/consumed。非NULL先空 max 值，`.WRITE(chunk,NULL,0)` 追加；每個 batch 驗證上一狀態，以短 transaction 原子更新值與計數，錯誤 rollback 後標記失敗再拋出。NULL 不用 `.WRITE(NULL,...)` 代替。
- [ ] **4. 實作最後 guard 與目標插入。** 同一短 transaction 驗證所有欄位完成、DATALENGTH、chunk數、順序及未 consumed，才 INSERT 並 consumed。staging缺失、缺段、重複、錯序、轉型失敗均無目標半列；不要用 TRY_CONVERT 的 NULL 吞錯。TRY/CATCH 每個 batch 完整閉合，變數不跨 GO；清除已插列內容但保留必要的消費狀態以拒絕重送最後批次。一般多列還原不承諾全檔單一 transaction。
- [ ] **5. 跑 lob／values groups 為綠並提交。** 檢查輸出每個 statement／batch實際UTF-8大小；不以擴高16MiB門檻充當分段。建議訊息 `feat(mssql-export): reconstruct large values with guarded SQL chunks`。

## Task 6：有界檔案驗證、安全發布與鏡像

**Files:** Create `lib/MssqlExport/MssqlExportArtifact.cs`, `lib/MssqlExport/MssqlExportMirror.cs`; Extend Models、測試、csproj。

**Interfaces:**

```csharp
// MssqlExportArtifact : IDisposable owns only its job temp.
internal static ExportDestinationStamp CaptureDestination(string path, CancellationToken cancellation);
internal static MssqlExportArtifact Create(MssqlExportRequest request);
internal Stream Output { get; }
internal MssqlArtifactReceipt Complete(IReadOnlyList<MssqlTableRowCount> rows, CancellationToken cancellation);
internal void Verify(MssqlArtifactReceipt receipt, Action<long, long> progress, CancellationToken cancellation);
internal string Publish(ExportDestinationStamp expectedDestination, CancellationToken cancellation);
// MssqlExportMirror
internal string CopyVerified(string publishedPath, MssqlArtifactReceipt receipt, MssqlMirrorOptions options,
    Action<long, long> progress, CancellationToken cancellation);
```

`MssqlArtifactReceipt` 定義 job ID、temp/published identity、long bytes、SHA-256、table/row totals、完成標記。完成標記不包含自己的 hash；receipt 保存包含標記在內的最終 hash。

- [ ] **1. 寫失敗測試，group `artifact`。** 以全新 fixture 建「舊版」與「他人暫存檔」，注入 truncate／disk-full／rename失敗／cancel／同意後換檔：
  ```csharp
  Assert(File.ReadAllText(oldFixturePath) == "old fixture", "Failed publish keeps existing file");
  Assert(File.Exists(otherJobTemp), "Cleanup owns only this job temp");
  Assert(pruneCalls == 0, "Failed mirror never prunes old backups");
  Assert(streamReadMax <= 81920, "Verification uses fixed buffers");
  ```
  ReadAllText 只可用於這幾個 bytes 的測試哨兵檔；產品 verifier 及大檔測試禁用。
- [ ] **2. 跑 artifact group 為紅。** 增加同目錄mirror no-op、mirror target已存在不覆蓋、cleanup失敗殘留路徑、發布後cancel結果仍有PublishedPath。
- [ ] **3. 實作 temp／hash／驗證。** 目標同目錄 CreateNew，名稱為隨機 `.mysqlpunk-export-<job>.partial`，不帶 `_backup_` 等 retention分類片段；UTF-8無BOM writer、incremental SHA-256與long計量、80KiB重讀。完成標記只在所有來源／DDL寫出成功才寫；關閉flush成功後才能verify。
- [ ] **4. 實作發布邊界。** 正規路徑同一工作／process互斥，最後取消與destination stamp核對後進短不可取消區；不存在用不可覆寫Move，存在用File.Replace，保留可恢復位置。目標變更、鎖定、FS不支援replace均fail closed，不退回delete-then-move。以 Windows 實測核對外部競態；不得把process內互斥宣稱成所有程序的檔案鎖。清理只刪本工作明確owned暫存；已發布／可恢復檔不誤刪。
- [ ] **5. 實作鏡像。** 串流copy到目的地獨有partial，驗證同receipt hash與長度，發布唯一正式名後才呼叫 `BackupRemoteMirrorService.PruneRemoteBackups(directory, retainCount)`。保留原分類規則，同時不可讓其他活躍工作partial被分類清掉；不呼叫舊MirrorBackup。錯誤／取消回worker以LocalCompletedMirrorIncomplete呈現。
- [ ] **6. 跑 artifact group 為綠並提交。** 建議訊息 `feat(mssql-export): verify and publish exports and mirrors safely`。真UNC/權限/磁碟滿與發布競態仍列Task10。

## Task 7：worker、進度／ETA 與取消 orchestration

**Files:** Create `lib/MssqlExport/MssqlSqlExportService.cs`, `lib/MssqlExport/MssqlExportProgressEstimator.cs`; Extend Models、測試、csproj。

**Interfaces:**

```csharp
internal MssqlSqlExportService(Func<IMssqlExportSource> sourceFactory);
internal MssqlExportCatalog LoadCatalog(string databaseName, CancellationToken cancellation);
internal MssqlExportPreflight Preflight(MssqlExportRequest request, CancellationToken cancellation);
internal MssqlExportResult Export(MssqlExportRequest request,
    IProgress<MssqlExportProgress> progress, CancellationToken cancellation);
// MssqlExportProgressEstimator: caller supplies elapsed time, tests use a fake clock.
internal MssqlExportProgressEstimator(Guid jobId, MssqlExportSchema schema, bool includeMirror);
internal MssqlExportProgress Sample(MssqlExportStage stage, MssqlObjectKey currentTable,
    long currentTableRows, long currentTableSourceBytes, long writtenBytes, TimeSpan elapsed, bool force);
```

`currentTableRows/currentTableSourceBytes`為目前表累計，`writtenBytes`為全檔累計；Sample未達節流時可回null，force必須回snapshot。estimator從schema取得nullable估計，切表時記錄已完成實際數；不可自行查DB。LoadCatalog／Preflight都各開短生命週期source並dispose。

- [ ] **1. 寫失敗測試，group `worker`。** fake source記錄open/begin/read/complete/dispose與檔案步驟順序；估計用fake clock，不sleep。assert：
  ```csharp
  Assert(calls.IndexOf("source.dispose") < calls.IndexOf("artifact.verify"), "Release snapshot before local work");
  Assert(samples.Where(x => x.Stage != MssqlExportStage.Completed).All(x => x.Percent == null || x.Percent <= 99), "No early success");
  Assert(initial.Eta == null && stalled.Eta == null, "No fabricated countdown");
  Assert(result.PublishedPath != null && result.Stage == MssqlExportStage.LocalCompletedMirrorIncomplete, "Keep local outcome");
  ```
- [ ] **2. 跑 worker group 為紅。** 讀取／寫入／verify／mirror取消，發布區取消，舊operation回報、DB斷線、schema指紋改變、實際rows超estimate、不同列寬、缺失估計都覆蓋。
- [ ] **3. 實作階段順序。** 新source重做Inspect→BeginRead→DDL前段→逐表data→DDL後段／schema核對→CompleteRead與source Dispose→artifact Complete/Verify/Publish→選用mirror→結果。預檢失敗不建立partial；UI先前Preflight不跳過開始時的重查。SchemaOnly不讀rows；DataOnly不寫DDL。失敗停止整個工作，不跳表或重連續跑；finally reader/command/transaction/owner及本工作temp順序清理。
- [ ] **4. 實作進度。** 預設200ms節流，階段／表／terminal強制回報；使用最近10秒樣本，至少3個樣本且至少2秒才估ETA。連續5秒無實際進展時ETA回null並顯示等待。估計remaining work按表估計bytes及實測row-width調整，不按表數等權；估計失準或劇烈速率變化可退回未知。局部verify/mirror以實際bytes計算，整體百分比單調且成功前≤99，不重置0；terminal狀態立即回報。UI log採上限200條。
- [ ] **5. 跑worker group為綠並提交。** constructor default factory只建立自己的session；不讀全域backup設定、不觸碰共享provider。建議訊息 `feat(mssql-export): orchestrate cancellable export with measured progress`。

## Task 8：正式精靈與 Punky

**Files:** Create `mySQLPunk/MssqlExportWizardForm.cs`; Extend 測試、csproj。重用現有 `PunkyReportLoadingControl`、ThemeManager、UiKit，不修改報表精靈。

**Interfaces:**

```csharp
// MssqlExportWizardOptions (Models): mode, initial schema/name selections,
// includeViews default, default file name, optional mirror; immutable.
internal MssqlExportWizardForm(Dictionary<string, object> connection, string databaseName,
    MssqlExportWizardOptions options);
internal MssqlExportResult Result { get; }
internal MssqlExportWizardForm(Dictionary<string, object> connection, string databaseName,
    MssqlExportWizardOptions options, MssqlSqlExportService service);
```

- [ ] **1. 寫失敗測試，group `wizard`。** 在STA透過fake service，不連DB；預設全選／單表預選、搜尋comment保持勾選、同名表不同schema、零選取／空路徑阻擋、mode切換views、snapshot不足、reset舊結果、關閉等cleanup。assert：
  ```csharp
  Assert(selectedObjectIds.SequenceEqual(expectedObjectIds), "Search keeps object identity");
  Assert(!startButton.Enabled, "Failed preflight blocks start");
  Assert(!form.IsDisposed, "Close waits for worker cleanup");
  Assert(soundButtons.Count == 1, "Mute only; no play or pause");
  ```
- [ ] **2. 跑wizard group為紅。** UI自動化以控件Name/AccessibleName與注入service驅動；不靠像素坐標斷言全部版面，也不把fake進度當真匯出。
- [ ] **3. 建立精靈。** 參照已驗收index.html：選表→輸出設定→確認／預檢→進度／結果；table list顯示schema/name/comment/nullable estimates。Windows SaveFileDialog、背景捕獲destination stamp、庫級結構views預設勾選；確認頁明列SNAPSHOT與logical export範圍／mirror。DPI、working-area clamp、可捲內容及固定footer沿用現有UI模式，中英文沿用ReportText.Get。
- [ ] **4. 接worker生命週期。** constructor只複製scalar connection settings並剔除pdo/isConnect/runtime state；Shown背景LoadCatalog，確認頁背景Preflight，改選項使預檢失效，通過才啟用開始；開始時freeze request。UI context建立Progress，Task.Run執行service；只接受同JobId且視窗存活的進度。UI timer只更新elapsed，距離最近實際進展5秒後將ETA顯示為等待／未知，不在阻塞reader時繼續假倒數，也不虛增bytes或百分比。執行禁改選項；取消／關閉等待worker，terminal才EndExport及disposeCTS。開始BeginExport一次、只有Muted控制，取消靜音不重播原音。
- [ ] **5. 跑wizard group為綠，做本機WinForms人工檢視再提交。** light/dark、最小可用工作區與100%/150%/200%DPI、長table/comment、未知ETA、cancel與關閉按鈕可達。實際聽到聲音另記證據；未可觀察不聲稱通過。建議訊息 `feat(mssql-export): add native export wizard and Punky progress`。

## Task 9：四個互動入口接線與 provider 回歸

**Files:** Create `mySQLPunk/Form1.MssqlExport.cs`; Modify `mySQLPunk/Form1.cs` 四個handler、csproj；Extend測試。

**Interfaces:**

```csharp
// Form1 partial; TreeDatabaseTarget is the existing private nested type.
private void OpenMssqlExportWizard(TreeDatabaseTarget target,
    MssqlExportMode mode, string initialSchema, string initialTable, bool backupEntry);
```

- [ ] **1. 寫失敗測試，group `routing`。** 用mode/options捕獲 seam驗證四個handler在MSSQL時只開一次wizard、單表只預選指定schema/name、DataOnly保留、backup才帶mirror，view單物件不變成全庫；MySQL/SQLite/PostgreSQL仍走既有路徑。
- [ ] **2. 跑routing group為紅。** 測到真正handler分流／options，不只搜尋source中的方法名字。
- [ ] **3. 在既有SaveFileDialog／同步dump之前分流。** 使用target.ProviderName識別MSSQL，不能 `target.Database is my_mssql`，SSH會包wrapper。target.ConnectionInfo剔除runtime物件後交form，backup入口捕獲Directory/RetainCount並使用_backup_檔名；其餘export傳null mirror。指定單表按metadata解析原始schema/name，object_id由catalog確認，不從顯示comment反推。
- [ ] **4. 收斂完成訊息。** handler根據wizard.Result更新main status，不無條件再跳「SQL已匯出」；使用者取消設定時無成功訊息。本機完成但mirror未完成顯示精確狀態。維持view-only舊入口、非互動backup caller及其他provider不變。
- [ ] **5. 跑routing、wizard與既有dump/native-backup/connection groups，再Debug/Release build後提交。** 建議訊息 `feat(mssql-export): route MSSQL interactive SQL exports through wizard`。

## Task 10：隔離 DB 還原、大資料與交付證據

**Files:** Create `tests/SmokeTests.MssqlExport.Integration.cs`, `tests/Run-MssqlExportIntegration.ps1`, `docs/MSSQL_SQL_EXPORT.md`, `docs/design/mssql-export/verification.md`; Modify smoke registration/runner source list、必要的純fixture忽略項、現有history。不加入raw SQL大檔或credentials。

**Interfaces:**

```powershell
# Runner defaults to instructions/no execution unless -Run is explicit.
param([switch]$Run, [string]$Server,
      [ValidateSet('Core','Large','Concurrency','Ssh')][string]$Profile = 'Core',
      [string]$OutputDirectory)
```

runner要求明確server及全新／空output目錄，Integrated Security為第一版fixture認證。若測試環境只有其他認證，執行時補非command-line秘密輸入方式；不讀使用者已存連線或把密碼寫入args/log。不自動下載容器、安裝／啟動DB。明確記錄server與sqlcmd版本／architecture／資料庫collation與profile，敏感資訊不輸出。

- [ ] **1. 寫與執行Core restore失敗測試。** 真實產物由新service生成，透過一次 `sqlcmd -S <explicit-fixture-server> -d <generated-target-db> -E -b -x -f 65001 -i <sql-path>` 還原；不使用mySQLPunk舊script splitter。來源／目標DB名必須是此次runner產生的 `mysqlpunk_export_<guid>_src/_dst`，清理前核對server、完整名稱及ownership token，絕不DROP傳入任意DB。runner預設只介紹用法、不建庫；-Run後只操作自己建的fixture。
- [ ] **2. Core矩陣。** 兩個schema同名表、Unicode／`]`識別字；SQL型別精度／offset／collation／NULL／empty／tailspaces；keys/constraints/indexes/views/comment/identity正負increment／空表與新增；computed/rowversion預期；geometry／geography和其他unsupported預檢拒絕。對reader不可見definition／snapshot off／版本缺欄位也保留案例。使用catalog比較及streaming value hash，不只比行數。
- [ ] **3. 大欄位restore矩陣。** 至少64MiBbinary、超過16MiBUnicode、多LOB/無PK/legacy text/ntext/image/untyped XML、不同tempdb/source collation及長WKT普通文字。assert還原DATALENGTH／streamingSHA-256相同；`GO`、`:r`、`$(name)`、NUL／surrogate不被工具展開。對fixture產物副本移除／重複／調換chunk，故意conversion error與錯誤後繼續的client，assert失敗列沒有新增／沒有NULL替代。測重複最後batch不再插入；成功先前列可存在，不把整份還原稱all-or-nothing。
- [ ] **4. Large profile。** 持續產生而非一次配置大buffer，輸出檔實際 `Length > 2147483648`；10倍來源對照記錄peak working set/private bytes、rows、source/export bytes、elapsed與versions。出口／verify／mirror各自sampling，確認無隨總量線性成長；若峰值差異異常需分析，不只用含糊「跑完」。>1GiB單欄／接近型別理論上限另列optional stress，未跑保留pending，不能阻擋使用者已同意的v1範圍。
- [ ] **5. Concurrency profile。** 第二連線以一個transaction同步修改父子表，驗證export同一快照；途中DDL變更須失敗；等待command取消／連線斷線、IO中斷／磁碟滿或受控故障stream、verify/mirror取消、Windows覆寫競態均核對舊檔與結果。來源交易必須在file verify前已釋放。設定snapshot只限自有fixture。
- [ ] **6. Ssh與人工UI。** 明確測試SSH端點提供後驗證同job tunnel成功／取消／失敗釋放且main connection仍可用；缺環境保持pending。Native UI實際大資料作業、聲音一次／mute、長時間回應性另外列結果，不用HTML模擬截圖替代。
- [ ] **7. 正式本機檢查。** 所有定向cases通過後，保存／清除既有filter跑一次完整既有smoke、`build.bat Debug`、`build.bat Release`、`git diff --check`。檢查release既有CodexVerify／test EXE排除，fixture只在ignored全新目錄；必要時增filename正反案例，不為本功能執行packaging／release。任何AV攔截均停止該測試管道並記pending。
- [ ] **8. 文件與證據。** 操作文件寫實際已測sqlcmd命令、SNAPSHOT前提、支援／不支援能力、部分restore語意／資源成本、GUI匯入未驗證範圍、結果與取消文案。verification逐項填PASS/FAIL/PENDING、命令、版本、產物hash、記憶體記錄與未驗證理由；保留大檔local-only。history追加精準結果。
- [ ] **9. 最終review及提交。** 依選定執行方式進行一次整體review，修正實質問題後只重跑受影響檢查；不循環產生無實質發現的review。建議訊息 `test(mssql-export): verify isolated restore and large export behavior`。未完成的DB／SSH／UI驗收保持明列，不宣稱功能全數驗收；merge/push/tag/release需另外授權。

### 預定驗收命令（本文件尚未執行）

```powershell
.\build.bat Debug
.\build.bat Release
.\tests\Run-SmokeTests.ps1 -Configuration Release
# <...> 是執行時明確選定的隔離環境與全新目錄，不能原樣執行。
.\tests\Run-MssqlExportIntegration.ps1 -Run -Server '<fixture-server>' -Profile Core -OutputDirectory '<new-empty-core-dir>'
.\tests\Run-MssqlExportIntegration.ps1 -Run -Server '<fixture-server>' -Profile Large -OutputDirectory '<new-empty-large-dir>'
.\tests\Run-MssqlExportIntegration.ps1 -Run -Server '<fixture-server>' -Profile Concurrency -OutputDirectory '<new-empty-concurrency-dir>'
git diff --check
```

## 執行順序與 handoff

- 主線順序：1→2→3→4→5→6→7→8→9→10。每task測試紅→綠與diffreview後形成可追溯提交；尚未接線時不對外宣稱完成。
- 可並行部分：Task 6在Models／介面固定後，可與3–5的writer工作並行；不要同時修改Models、csproj、同一測試partial。其他task按依賴執行。Task10的fixture準備可以提前，但真restore證據必須使用最終writer。
- 建議分工實作：此功能的錯誤可能造成無法還原的備份，適合每個可測交付獨立review；主agent管理shared介面與最終整合。使用者也可選擇主agent依序實作、完成後整體review。
- 執行前檢查當時git status與既有worktree附件，依using-git-worktrees流程選隔離checkout；必須攜帶本spec／plan／既有新prototype檔，不能在新worktree漏掉未提交設計。不擅自stash/reset或帶入無關Desktop Properties。
- 計畫審閱後才開始產品實作。本次只寫規格狀態、plan、README導航與history；沒有build、EXE、DB、commit或push。

## 自查（計畫撰寫時）

- Spec 2–4／Punky對應Task8–9；spec5對應Task1–2、7；spec6對應Task2–5、10；spec7對應Task7；spec8–9對應Task6–7；spec10證據對應Task10。
- 四條互動入口包含獨立的單表handler；view-only、非互動backup、其他provider保持原路。覆寫同意／partial ownership、mirror驗證後prune均有獨立測試。
- Review Focus五項均已分配owner與測試。接口一致採Models／IMssqlExportSource／MssqlRowReader／receipt／request；沒有混用原shared IDatabase當worker connection。
- 編譯與fake cases、native UI、實際SQLrestore／大資料／持續寫入／SSH證據分開。未執行步驟沒有勾選，理論型別上限沒有宣稱已驗證。
