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

Live cleanup and cancellation prove successful cleanup and truthful uncertainty. A naturally occurring provider SqlError during rollback/owner disposal was not induced; its complete-error contract has adapter/worker/log regression evidence only. Owner-release diagnostics and composite-FK changes were followed by offline checks and small Core. The review repair was followed by Faults22 and focused offline checks; large profiles remain the historical measured evidence above. Full unrelated regression, native UI/audio/DPI, SQL Server 2019/Transfer and the user's 4.17 GiB dump, browser/IIS/production remain separate acceptance work. No user dump was opened or executed by Task 6.

Task 6 independent scoped re-review approved I1/I2 at `5c848ae`. The terminal truncation fix at `152e419` passed its actual-service/native UI regression plus13wizard/19worker groups. Whole-branch and SQL2019 Transfer acceptance remain Task7.

## SQL2019 Transfer trial: Phase A / runtime pending

Task7 is a separate one-shot user-file acceptance harness, not a LocalDB fixture profile. `tests/Run-MssqlImportTransferTrial.ps1` requires explicit `-Run -Server localhost -Database Transfer -InputFile C:\Users\johnho\Desktop\Transfer_dump_20261013_165730.sql -OutputDirectory <new-empty-C-path>`. It cannot start an instance, clear/drop Transfer, change recovery/constraints/permissions or retry. The fixed C-only atomic attempt marker is independent of output directory and remains after every claimed outcome; any existing/corrupt/interrupted marker blocks reuse.

Phase A local evidence: Debug and targeted Release builds plus the registered C# Transfer guard group passed, including strict bounded JSON, exact measured product/smoke executable pin predicates, SID/name mismatch, owned-token monitor cancellation on <20GiB/IO failure, tiny owned-marker CreateNew/race/unchanged-existing cases and primary-failure retention. SQL factories were explicitly blocked in this offline group; no SQL Open occurred. The actual runner NoRun with foreign opt-ins set returned SKIPPED before source/target work; wrong-server `-Run` rejected before pin/child/source/SQL. Windows PowerShell 5.1.26100.9549 passed 103 pure decision assertions plus seven strict JSON negatives. Existing opt-ins/filter were saved/restored; AutoReleasePolicy passed. Exact final source/build/pin evidence is recorded in the local Task7 handoff, outside Git.

Runtime release is still pending. The candidate source/executable/product/registration pin must remain unaccepted until the controller's narrow review. This Phase A does not open or fully scan the 4,478,414,151-byte user file, connect SQL2019, create a trial marker or write Transfer. Historical native footer 126 tables / 3,247,785 rows is an expectation, not observed target data. Actual same-handle scan SHA/batch count/max code units, source READ COMMITTED warning, SQL2019 identity/permissions/empty-target proof, used-log before/after metrics, result/cleanup and independent COUNT_BIG/catalog receipts are pending. Count equality is not source field-hash or source-definition equality. Human sound/interaction, physical DPI and production/browser/IIS remain separate unverified boundaries.

Final unfiltered Release smoke, build.bat Debug/Release and whole-branch review remain later acceptance steps. No push/merge/release is implied by the local checkpoint.
