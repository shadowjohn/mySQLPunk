# Native MSSQL import acceptance

Task 6 was run on the dedicated `(localdb)\mySQLPunkExportTest_20261012`, SQL Server 17.0.4025.3, using the actual importer and native exporter. All 65 expected case IDs in `tests/MssqlImportFixtures/expected-cases.json` matched executed PASS receipts. Every profile and both memory children confirmed fixture cleanup. Raw scripts, logs, hashes and receipts remain local on C, outside Git.

## Reproduce

The controller must first inspect the approved manifest and start its exact dedicated instance. The runner checks that it is already running before opening SQL connections and never starts it.

```powershell
powershell -NoProfile -File tests/Test-MssqlImportIntegrationGuards.ps1
powershell -NoProfile -File tests/Run-MssqlImportIntegration.ps1 -Run -AuthorizationPath <approved-manifest> -Profile Core -OutputDirectory <new-empty-C-fixture-descendant>
# Repeat with Lob, Geometry, Faults and Memory, each in a new output directory.
$env:MYSQLPUNK_SMOKE_FILTER='MSSQL import'
powershell -NoProfile -File tests/Run-SmokeTests.ps1 -Configuration Debug
powershell -NoProfile -File tests/Run-SmokeTests.ps1 -Configuration Release
```

`-Profile All` also runs all five profiles. The runner restores environment flags/filter in `finally`. Missing, skipped, incomplete, wrong-run or mismatched case receipts fail acceptance; executable and case-receipt SHA256 values are checked. Ordinary offline runs skip the opt-in live entry and are not live acceptance.

Fixture creation uses new GUID database names and exact C descendant MDF/LDF paths. Cleanup requires the generated/created allowlist, private owner.token, matching database extended property, and the exact two physical files with reparse checks. No existing user database is a cleanup target. Permission cases create users WITHOUT LOGIN and narrow roles/GRANT/DENY only inside these fixtures; impersonation uses nonpooled owned connections with REVERT/finally close. No server login, snapshot, recovery or KILL changes are required.

## Evidence from 2026-10-13/14

The evidence base is `C:\Users\johnho\AppData\Local\Temp\mysqlpunk-export-20261012-7cadb673332543c3b17c61924490a702`. Each relative output below contains `<Profile>/profile-receipt.json`, the exact case receipts, result JSON and complete error logs.

| Profile | Matched PASS cases | Relative output | Main checks |
| --- | ---: | --- | --- |
| Core | 12 | import-core-final-b7e2fa6305464e4bb5f18aa9a3a534de | Full/SchemaOnly/DataOnly, 12 tables/15 rows, identities, indexes, FK trust including composite FK, views/properties, same-session temp/SET/identity/transaction/multiple results, parser no-write and target recreation |
| Lob | 14 | import-lob-2d8d118a9e714bb09a711d2cbaef67bb | 64 MiB binary, 18 MiB Unicode, NULL/empty/XML bytes/hashes, DataOnly; ten corruption/failure cases under both skip policies |
| Geometry | 11 | import-geometry-a72bcca2f4ee4945b4c98f83762e4621 | Raw Serialize hashes, >16 MiB geometry, SRID/Z/M/curves/NULL/empty/invalid values, DataOnly; seven corruption/failure cases under both skip policies |
| Faults | 22 | import-faults-readonly-lease-c5aff2d055d94acd9b0d019ced823a9d | Partial writes stop/skip, all three provider errors and flush ordering, real transaction states 1/-1, EOF transaction, timeout -2, owned transport loss, log IO, cancellation at reader/probe/count stages, close/success races, permission-filtered metadata, read-only before/after preflight and recreated targets |
| Memory | 6 | import-memory-9d9b76eacca74e55b494272552b45c91 | 2.5 million rows and 64 MiB returned LOB drained with zero value getters; warmed separate x64 streaming children and comparison |

Selected input artifacts (full SHA256 values are also in result receipts):

| Artifact | Bytes | SHA256 |
| --- | ---: | --- |
| Final Core full | 65,270 | 2f6d2102b149605e2a8f4ca48b35b9734e90141cb39d91b0c9f6f5e631e40750 |
| Lob full | 174,573,590 | 26142c71de9a3acceee96037ae884b0f6147fa080f6ad419e31313473c0ac085 |
| Geometry full | 36,193,436 | bc39f43927d2284d8a79b47bf6ef99651c07e560e6ceb92c635c2ab69277a093 |
| Memory 1x | 268,443,904 | 62c2cd573a5f692c1dd1ad4e3461e77773f2e31060224875125aec829ce98a34 |
| Memory 10x | 2,684,439,040 | e6f4c9c45c2749346e028732d35c4274d19463f2ead6d6c51ff66b6d6a494052 |

Memory children use identical approximately 1 MiB batch shapes: 256/2,560 batches. Private-byte peaks were 90,361,856 (~86.2 MiB) and 81,547,264 (~77.8 MiB); working-set peaks were 102,776,832 and 91,635,712. Baseline-adjusted peak difference was -9,043,968 bytes, below the 268,435,456-byte investigation threshold. This is the measured result for these fixtures, not a universal memory ceiling. `Result.Elapsed` was 27.256 s / 261.797 s for Run execution, verification and cleanup, excluding preflight. Parent case durations, including child startup and scanning, were 55.949 s / 518.074 s. Stage aggregates and 25 ms peak samples are retained in memory-comparison.json/memory-measurement.json.

The original Task 6 offline Debug and Release commands each passed 38 matched entries (including the inert live entry), plus AutoReleasePolicy. Nineteen worker groups and five parser groups include regressions for the corrections below. Pure PowerShell authorization/cleanup/receipt guards also passed without SQL. Local ignored logs are `.superpowers/sdd/implementation-plan/task-6-import-final-{debug,release}-green.log`, `task-6-runner-guard-final-green.log`, and `task-6-core-final-runner.log`.

## Corrections established by failures

- On this SQL17 instance, `SELECT DB_ID(),@@TRANCOUNT,XACT_STATE()` on a fresh session returned transaction count 0 with XACT_STATE 1. A state-only SELECT returned 0/0. Capturing DB_ID in a completed DECLARE statement before selecting transaction state restored the observed caller state without normalizing it. Initial native full import failed EofTransactionOpen; the corrected actual full import passed. Real open/committable/doomed transaction fault cases still stop. This observation is not claimed for every SQL version.
- Native SchemaOnly FK actions were mistaken for DELETE/UPDATE data. A bounded FK/REFERENCES/action context now recognizes NO ACTION/CASCADE/SET NULL/SET DEFAULT, including composite column lists. Exact parser RED/GREEN and final Core cover this; genuine DML after SET ... ON and after FK clauses remains rejected.
- Cleanup rollback and owner-release SqlError details previously disappeared. Immutable current-cleanup details now reach the complete log as Origin=Cleanup with zero source coordinates and DuringBatch before terminal flush. Typed fake-command/fake-owner adapter regressions and the actual worker/logger prove detail retention, merged ordering, cached-receipt deduplication and unchanged cleanup truth; the bounded asynchronous publication contract is described below. The pending cleanup assertion now waits for adapter publication, with a deterministic owner pre-return race test.

Failed receipts were retained: the initial state-probe failure, a test observer sharing failure (fixed only FileShare.ReadWrite), and an invalid fixture CREATE SCHEMA batch (fixed fixture setup). The runner also rejected a stopped instance before SQL or output creation; the controller restarted the exact authorized instance, and only small Core was repeated. The stop cause was not established.

## Review repair I1/I2

The initial Faults receipt `import-faults-7578048db9354ebfa6b6cef5d8ac5215` covered recreation only. Review found the missing live read-only coverage and an inverse cancellation race in release diagnostics. Both were repaired together after the separate Task 5 terminal-log fix, based on `152e4192d9e775b9529ea1653d357be72ad5ad1f`.

For I1, the provider can unwind before a watchdog's owner.Dispose completes. Cleanup now waits under the existing gate for release publication before its immutable final receipt/log, using a monotonic deadline of 10 seconds in production and Monitor.Wait/Pulse. Details delivered within that window appear exactly once before End. Expiry produces explicit SessionReleasePending and uncertain cleanup; release details are not yet available, and no late logger, repeated Dispose or unbounded watcher is created. A deterministic actual-service/adapter test with typed fake-owner errors first failed because the worker finished before publication, then passed with complete two-error logging. Its second order holds the owner past a 150 ms test window, verifies bounded pending completion, observes late publication under the gate, and proves no redispose or post-terminal log mutation. Actual RED/GREEN logs: `task-6-fix-round1-inverse-{red,green}.log` under the ignored plan directory.

For I2, the existing `faults.target-unavailable-boundary` case now includes read-only before preflight, read/write-to-read-only after preflight, and the original recreation. Every fixed READ_ONLY/READ_WRITE WITH NO_WAIT transition freshly checks the private token, DB ownership property, generated/created name and exact C physical files. Both read-only paths restore READ_WRITE in finally. The final Faults output above proves:

- `target-readonly-preflight.json`: Passed=false, Writable=false, TargetReadOnly, independently checked zero source writes.
- `target-{probe-open,before-run,factory-open-3}.json`: fresh MASTER is_read_only=true; `target-sut-3.json`: actual adapter Writable=false with the exact target identity.
- `result-target-readonly-after-preflight.json`: Failed/TargetReadOnly, ProcessedBytes=0, SuccessfulBatches=0, FailedBatches=0 and all cleanup flags true. `target-readonly-effects.json`: source table absent; equivalent direct CREATE through a fresh independent SqlConnection failed with SQL 3906.
- The separate fixture probe holds no active command or transaction across SUT execution, then closes before finally restoring READ_WRITE. `target-restored-3.json` confirms is_read_only=false. Recreation still rejects TargetChanged with zero processed bytes; profile cleanup confirms no remaining owned DBs.

Two failed reproduction leaves are retained: `import-faults-fix1-a6a5c4afb0174212a3ef818e9677bdcc` and `import-faults-readonly-observe-91859e49728e4a5488f8dd1a17449a42`. Without the probe lease, the fixture had already reverted to READ_WRITE before SUT Run; the importer read the current writable state and CREATE succeeded. This was not a demonstrated importer gate failure. AUTO_CLOSE=true was observed, but the reversion's cause was not established and AUTO_CLOSE was not changed. The probe lease maintains the deliberately read-only fixture state without weakening the expected rejection.

Repair Phase A Debug and Release each passed 39 importer entries plus AutoReleasePolicy (`task-6-fix-round1-offline-{debug,release}-green.log`); the count includes the accepted Task 5 regression. After the final live fixture implementation, its Release build/guard entry passed, final workers19 and parsers5 passed, and pure PowerShell guards passed again (`task-6-fix-round1-{lease-build,worker-final-green,parser-final-green,pure-guards-final-green}.log`). Final Faults22 exact IDs/case SHA/executable SHA/Confirmed cleanup were checked; executable SHA256 was `632157dbfc2c513d4d5130bc1868726823991fc0134f0496ec4aed9846b8c710`. Other live profiles and giant memory files were not rerun for this repair.

## Boundaries

Live cleanup and cancellation prove successful cleanup and truthful uncertainty. A naturally occurring provider SqlError during rollback/owner disposal was not induced; its complete-error contract has adapter/worker/log regression evidence only. Owner-release diagnostics and composite-FK changes were followed by offline checks and small Core. The review repair was followed by Faults22 and focused offline checks; large profiles remain the historical measured evidence above. At the Task6 checkpoint, full unrelated regression, native UI/audio/DPI, SQL Server2019/Transfer and the user's 4.17 GiB dump, browser/IIS/production remained separate acceptance work. No user dump was opened or executed by Task6; the actual Task7 outcome is recorded below.

Task 6 independent scoped re-review approved I1/I2 at `5c848ae`. The terminal truncation fix at `152e419` passed its actual-service/native UI regression plus13wizard/19worker groups. Whole-branch review and SQL2019 Transfer were deferred to Task7; its actual stopped-failure boundary is below.

## SQL2019 Transfer trial: guards and actual stopped failure

Task7 is a separate one-shot user-file acceptance harness, not a LocalDB fixture profile. `tests/Run-MssqlImportTransferTrial.ps1` requires explicit `-Run -Server localhost -Database Transfer -InputFile C:\Users\johnho\Desktop\Transfer_dump_20261013_165730.sql -OutputDirectory <new-empty-C-path>`. It cannot start an instance, clear/drop Transfer, change recovery/constraints/permissions or retry. The fixed C-only atomic attempt marker is independent of output directory and remains after every claimed outcome; any existing/corrupt/interrupted marker blocks reuse.

Phase A local evidence: Debug and targeted Release builds plus the registered C# Transfer guard group passed, including strict bounded JSON, exact measured product/smoke executable pin predicates, SID/name mismatch, owned-token monitor cancellation on <20GiB/IO failure, tiny owned-marker CreateNew/race/unchanged-existing cases and primary-failure retention. SQL factories were explicitly blocked in this offline group; no SQL Open occurred. The actual runner NoRun with foreign opt-ins set returned SKIPPED before source/target work; wrong-server `-Run` rejected before pin/child/source/SQL. Windows PowerShell 5.1.26100.9549 passed 103 pure decision assertions plus seven strict JSON negatives. Existing opt-ins/filter were saved/restored; AutoReleasePolicy passed. Exact final source/build/pin evidence is recorded in the local Task7 handoff, outside Git.

Phase A ended before opening/scanning the user file or connecting SQL2019. The controller subsequently approved the narrow guard/monitor/metadata repair reviews and freshly verified 269 source files, six registration files, both manifests and both binaries before accepting the 224445b runtime pin. The actual one-shot outcome below supersedes the former runtime-pending boundary. Count equality is not source field-hash or source-definition equality. Human sound/interaction, physical DPI and production/browser/IIS remain separate unverified boundaries.

Final existing regression was completed before the actual data trial: unfiltered Release smoke240 PASS plus AutoReleasePolicy, build.bat Debug and Release each PASS. SQL opt-ins/filter were saved/cleared/restored; no live profile was repeated. The controller reports the whole-branch review through 56f4ddf and the scoped metadata repair review approved with no findings. No push/merge/release is implied by the local checkpoint.

### SQL2019 pre-write harness rejection and repair

The first released harness invocation retained `transfer-trial-20261014-0124/final.json`: `Executed=false`, `AttemptMarkerDurable=false`, `Status=NotStarted`, `FailureCode=TrialFailure:InvalidCastException`. It stopped in the read-only identity SELECT before monitor creation, source preflight/scan, fixed marker claim or service.Run. No source SQL was executed; this flow does not independently prove current target emptiness. The original output is retained and never relabeled PASS.

A narrowly authorized read-only provider-shape probe confirmed DB_ID ordinal2 is SQL smallint/System.Int16 (compatibility ordinal5 is tinyint/Byte). The harness had used GetInt32 directly; existing product metadata already used Convert.ToInt32. The actual DataTableReader Int16 shape reproduced the failure offline, then the shared numeric conversion fixed both identity and used-log DB_ID checks. Wrapper reporting now preserves the fixed child NotStarted failure instead of trying to open an absent marker. Focused Release C#51 assertions and AutoReleasePolicy PASS; WinPS106 pure decisions plus seven strict JSON negatives PASS. Candidate pins were revoked before rebuild, then freshly inspected and accepted by the controller at 224445b. No DB cleanup/settings change occurred. The unconsumed import authorization was used exactly once in the actual trial below.
### Actual one-shot SQL2019 result: Failed, partial state preserved

Local evidence is `transfer-trial-20261014-0133` under the ignored plan directory. The first `0124` pre-write output remains unchanged. At runtime source commit `224445b`, fresh integrated identity was `JOHN\MSSQL2019` / Transfer, SQL Server 15.0.2190.7, compatibility150, ONLINE/READ_WRITE/FULL. Native preflight proved visible zero user objects and table/view creation rights. The held 4,478,414,151-byte UTF-8 source scan completed with SHA256 `c84db0d32b4a9b3c2fd940eb809c724f67cd31d26a0f292fd31f11a65e364eff`, 206,409 batches and max4,194,300 UTF-16 code units. Its native READ COMMITTED warning remains applicable.

The durable fixed attempt marker was claimed and service.Run executed once. Stop-on-first-error rejected batch1481, source lines5131–5328, byte interval[1957461,2018937), SQL241/state1/class16: date/time string conversion failed. The service acknowledged1480 successful batches /1,957,461 successful bytes, one failed batch, zero skipped bytes and no uncertain batch. The final receipt is Failed / TrialSutNotCompleted; command ended, session released, transaction cleanup confirmed and cleanup errors0. Cleanup confirms owned session/transaction handling, not removal of already committed effects. Wrapper exit1 reports secondary `ReceiptType` because a Failed final has no success-only independent-count fields; this is not the underlying SQL failure or acceptance. Its final receipt and complete SUT log retain the actual failure and log path.

Total receipt wall time was199.847864s, including full preflight; SUT-reported elapsed was10.039839s. The197 one-second samples recorded last CPU189,422ms (sampled, not exact process-exit CPU), peak private172,118,016B and peak working set186,707,968B. First/last sampled data and log allocations were8,388,608B each, observed allocation growth0; terminal allocation was not separately sampled after exit. Actual used-log snapshots were909,312B before Run and3,751,936B at terminal (+2,842,624B). Minimum sampled C free space was159,196,868,608B. Scanning ETA remained null; later execution ETA was an estimate invalidated by the failure, not a completion promise.

An explicitly authorized fresh read-only independent probe matched the same server/database/id/create_date and current/original Windows SID/name, with dbo complete metadata visibility. Per-table COUNT_BIG measured126 tables /46 rows, not3,247,785. Catalog inventory contained126 USER_TABLE and203 DEFAULT_CONSTRAINT records; no indexes, views, FK, CHECK or extended-property names were present at this partial point. No FK/check trust state existed to compare. Private `partial-independent-{identity,summary}.json` and counts/catalog JSONL receipts retain the details without business row values. Object-definition/field-content equality is unverified. No source rewrite, target cleanup, recovery/constraint/permission change or Transfer retry occurred; the one actual import authorization is consumed and the failed partial state/marker remains preserved.

### Existing exporter legacy-date correction and bounded verification

A bounded read of only the61,476-byte failing source batch found seven-fraction ISO literal shape `####-##-##T##:##:##.#######` targeting three legacy datetime(scale3) columns. No business row values were printed. Read-only synthetic SQL2019 SELECTs confirmed seven-fraction TRY_CONVERT yields NULL for datetime/smalldatetime (direct CONVERT errors241/295), while three fractions convert; datetime2(7) accepts and retains seven. The existing exporter grouped these three types under seven-fraction formatting. Commit `ea733a6` splits datetime/smalldatetime to three fractions and leaves datetime2/datetimeoffset/time precision unchanged; importer execution semantics and the original user file remain unchanged.

Meaningful codec RED/GREEN covers invariant legacy literal width and stored SqlDateTime min/.003/.007/max ticks; existing seven-digit modern-type assertions still pass. Focused Release scalar1 + parser5 + worker19 groups passed, as did AutoReleasePolicy and both build.bat Debug/Release. SQL opt-ins/filter were saved/cleared/restored. No full240 suite or heavy live profiles were repeated for this local change.

Only the existing dedicated Core fixture was extended with two legacy date columns and min/max/NULL values, preserving12 expected case IDs and15 rows. Its first setup failed SQL207 because UPDATE was compiled with ALTER ADD in one batch; the failed leaf `import-core-legacy-date-3c6cae9db65342dcb9c97481acd4f951` records no cases and Confirmed cleanup. Commit `1ff20c9` separates the two owned-source Exec calls. Fresh compile/integration guard1/AutoReleasePolicy passed. The controller authorized one replacement Core, `import-core-legacy-date-790caeac0e5d438a8e5487cc265e6608`, on the exact owned SQL17 LocalDB; all12 IDs/statuses/case SHA256 values/executable SHA256 matched, complete=true, cleanup Confirmed. Actual full and DataOnly source/target raw bytes comparisons passed for the legacy date boundaries/NULL and existing modern dates. Full export was65,529B SHA256 `70e7e8a61ece756c30303a6cc119e6975a85d90ef0b58876cd5b88a0e529a296` /80 acknowledged batches; DataOnly58,384B /52 batches, both zero failures with confirmed session/transaction cleanup. This replaces the small Core evidence for the date fix; historical65-case evidence remains separate and no additional case IDs are claimed. It does not establish a successful reimport of the user's original dump.
