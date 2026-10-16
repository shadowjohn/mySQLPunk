# SQLite repair scoped review

Review date: 2026-10-16. Reviewed Task 2, `sqlite-repair-report.md`, the supplied SQLite repair patch, and the current scoped SQLite source. Static review only: no builds, test reruns, live databases, saved configuration, or other provider source were accessed. Existing private-fixture results were supplied by the implementer/root and are not independent runtime evidence from this review.

Decision: changes requested. The documented unsupported modules, encodings, size limits, savepoints, attached/temp schemas, arbitrary explicit-transaction atomicity, and spatial reconstruction limits were treated as acceptance boundaries, not findings.

## Material findings

### 1. [P1] Put strategy-generated DROP inside the managed transaction

Location: `mySQLPunk/lib/SqliteImport/SqliteImportRunner.cs:118-131` (DROP at 123; BEGIN at 131).

With an existing populated `t`, `DropAndRecreate`, `WrapInTransaction=true`, and a CREATE-only dump such as `CREATE TABLE t(x); INSERT INTO t VALUES(1); FAILING_SQL;`, the strategy executes its generated `DROP TABLE` while the connection is still in autocommit. Only afterward does it start `BEGIN IMMEDIATE`. A later failure rolls back the replacement table but leaves the original table and its rows permanently deleted. Begin the managed transaction before any policy-generated mutation. Add a private SQLite regression that retains the original table and data after a later failure, including a failing replacement CREATE.

### 2. [P1] Stop when a continued statement error has already rolled back the transaction

Location: `mySQLPunk/lib/SqliteImport/SqliteImportRunner.cs:129-148` (failure continuation at 142-143).

The runner keeps `txOpen=true` after every ordinary execution error. SQLite `INSERT OR ROLLBACK`/`RAISE(ROLLBACK,...)` can roll back the entire transaction. With wrapping and ContinueOnError enabled, `CREATE TABLE t(x UNIQUE); INSERT INTO t VALUES(1); INSERT OR ROLLBACK INTO t VALUES(1); CREATE TABLE leaked(x); INSERT INTO leaked VALUES(2);` rolls back `t`, but subsequent statements run in autocommit because the stale flag suppresses a new BEGIN. The import reports Failed while `leaked` persists, outside the documented earlier-explicit-COMMIT/wrapping-disabled boundary. Check native AutoCommit after a statement error and stop if the tracked transaction has disappeared; also reconcile state before executing further statements. Cover this with an actual private SQLite fixture.

### 3. [P1] Reject every close attempt while the worker is busy

Locations: `mySQLPunk/SqliteImportWizardForm.cs:129-136`, `mySQLPunk/SqliteExportWizardForm.cs:127-134`; newly owned-session callers at `mySQLPunk/Form1.SqliteImport.cs:33-38` and `mySQLPunk/Form1.SqliteExport.cs:46-51`.

The first window-close attempt sets `closing=true` and cancels that close, but a second close attempt while cleanup is still running bypasses `if (busy && !closing)` and closes the dialog. The new caller-owned session is then disposed while the background task can still execute SQL/rollback or post progress. Always set `e.Cancel=true` while busy; use `closing` only to avoid sending cancellation twice, and allow the completion/finally path to close after busy becomes false. This must cover both import and export. Import preflight also uses busy, so its finally path needs to honor a pending close after the scan ends.

### 4. [P2] Fail closed for unscoped sequence/comment mutations under preserving policies

Location: `mySQLPunk/lib/SqliteImport/SqliteImportRunner.cs:218-225`.

SkipExisting exempts the entire `sqlite_sequence` and comments sidecar from ordinary protected-table DML checks, then protects them only if a narrow literal-owner regex matches. A standard SQLite dump's `DELETE FROM sqlite_sequence;` therefore executes even when an existing AUTOINCREMENT table is being skipped; its restoring INSERT is then skipped because that INSERT names the protected table. For an existing table whose deleted high-water mark was 100 but whose current maximum id is 1, a subsequent insert can reuse 2 instead of preserving 101. Unscoped sidecar DELETE has the same ownership hole for unrelated comments. Reject or safely skip ambiguous mutations when protected owners exist, while retaining the exporter's narrowly scoped owner operations. Add a private fixture with a deleted sequence high-water mark and an unscoped DELETE.

### 5. [P2] Apply the new single-table initial options to the actual wizard controls

Locations: `mySQLPunk/SqliteExportWizardForm.cs:209`, `:219`, `:641-648`; caller at `mySQLPunk/Form1.SqliteExport.cs:34-36`.

The single-table route now sets IncludeViews/IncludeTriggers=false, but BuildPage0 still initializes both controls to true, and StartExportAsync rebuilds options from those controls. Consequently the actual dialog ignores the new safe trigger default and exports selected-owner triggers even when their bodies reference omitted tables. If the requested table is the database's only table, the view list is also re-enabled because catalog.All is true. Apply the supplied booleans to the controls so the normal dialog agrees with the injected-runner route. Verify the initial single-table control values and resulting service options.

## Scope completion

No additional material findings were identified in the scoped pass for the shared lexer, strict UTF-8/NUL representation, native bounded sequential row reads, generated-column projection, rowid/table_list metadata, sequence emission, checked source queries, snapshot/publication flow, or comments roundtrip beyond the preserving-policy hole above. This is a static review conclusion, not a new product build, UI, or production acceptance claim. The root owns shared-helper review and the authoritative final suite after the repair wave.

## Correction re-review verdict (2026-10-16)

Decision: all five findings are addressed; approve this scoped correction wave for specification and code quality. This verdict supersedes the earlier changes-requested decision for those five items. One static re-review was performed against `sqlite-corrections-diff.patch`, the current corrected source, appended implementer report, and the four regression test definitions. No builds, tests, live databases, or saved configuration were accessed during re-review. No broader new review loop was undertaken.

| Finding | Verdict | Current source evidence |
| --- | --- | --- |
| 1. Managed DROP ordering | Addressed | `SqliteImportRunner.cs:122-132` starts BEGIN IMMEDIATE before the generated DROP. The regression covers both a later failure and an invalid replacement CREATE while asserting the original populated table remains. |
| 2. Continued error after full rollback | Addressed | `SqliteImportRunner.cs:113-116` reconciles native AutoCommit before subsequent statements; `:145-148` stops immediately when an execution error ended the tracked transaction. Managed, explicit BEGIN, and RAISE(ROLLBACK) regression cases assert no later leaked table. |
| 3. Repeated close and pending asynchronous work | Addressed | Both forms' `:129-139` close handlers cancel every close while busy and request cancellation once. Import preflight `SqliteImportWizardForm.cs:569-576`, import execution `:692-699`, export catalog `SqliteExportWizardForm.cs:508-532`, and export execution `:714-721` release busy and honor the pending close after awaited work. Export catalog loading now holds the busy guard across all three provider queries. |
| 4. Unscoped protected metadata mutation | Addressed | `SqliteImportRunner.cs:232-269` permits only complete single-owner native-exporter sequence/comment forms when existing owners are present, skips ambiguous forms, and preserves existing owners. The regression covers broad DELETE, broadened WHERE, deleted high-water marks, unrelated comments, and allowed new-owner metadata restoration under both Skip and Keep. |
| 5. Single-table supplied flags | Addressed | `SqliteExportWizardForm.cs:96-98` applies the supplied IncludeViews/IncludeTriggers values to the actual controls. `:638-655` derives service options from those controls, and `:668` uses that method in execution. The one-table regression includes a view and a trigger with an omitted dependency and asserts neither is exported. |

No material direct breakage from these five corrections was found in this bounded pass. Runtime evidence remains attributed to its owners: the implementer reports isolated 16/16 against exact SQLite/form source, and the root reports a fresh actual-product build plus integrated SQLite 19 groups and AutoReleasePolicy passing (`repair-sqlite-final-tests.log`). This reviewer inspected the regression assertions but did not rerun them. Displayed WinForms/DPI interaction, live databases, and production acceptance remain outside this approval.
