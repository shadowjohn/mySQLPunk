# SQLite wizard repair report

Baseline: d41825cead810bc67f263753bb5f986a7f69c65c. Scope implements Task 2 of the approved repair plan. No user database, saved connection configuration, account state, extension module, commit, push or merge was accessed/performed by this worker.

## Changed files

- `mySQLPunk/lib/SqliteImport/SqliteImportModels.cs`: explicit visible conflict-choice mapping; synchronous OFF is no longer an implicit import default because the import wizard has no corresponding safety control.
- `mySQLPunk/lib/SqliteImport/SqliteImportPreflightScanner.cs`: one incremental strict UTF-8 lexer shared by preflight/execution, exact CR/LF characters, escaped quotes/backticks/brackets/comments, trigger BEGIN/END and nested CASE handling, 32 MiB decoded UTF-8 statement bound, bounded comment metadata, encoded manifest table names and verified payload checksum before target writes.
- `mySQLPunk/lib/SqliteImport/SqliteImportRunner.cs`: initial versus current object tracking, coherent DROP/CREATE/DML/dependent-index/trigger policies, checked provider status for wrapper/drop/cleanup operations, script transaction tracking and rollback, native interrupt support, checked original FK/synchronous restoration, failed tail/BEGIN/COMMIT/ROLLBACK and continued errors cannot produce Completed. A supplied native connection must initially be in autocommit mode. Shared sidecar CREATE IF NOT EXISTS remains idempotent and preserves unrelated comments.
- `mySQLPunk/lib/SqliteExport/SqliteSqlExportService.cs`: checked source snapshot/query/count/DDL operations, table_xinfo writable-column projection, table_list rowid metadata, implicit rowid and sqlite_sequence restoration, source-side exact TEXT/BLOB literals, filtered trigger dependencies, one sidecar-comments path, bounded native sequential reads, accurate delivered-file hash/length and shared atomic publication helper.
- `mySQLPunk/SqliteImportWizardForm.cs`: confirmation and execution use the same explicit conflict-choice mapping.
- `mySQLPunk/SqliteExportWizardForm.cs`: applies the requested initial table selection; subset exports omit the database-wide view list because dependencies cannot be inferred safely from that UI. Triggers are filtered against selected owners by the service.
- `tests/SmokeTests.SqliteImport.cs`, `tests/SmokeTests.SqliteExport.cs`: durable private SQLite fixtures and regression assertions; existing mocks now follow the actual catalog/PRAGMA/leading-whitespace contract.

The root owns dedicated raw job-session construction, routing, worker close/cancel integration, shared publication implementation and test registration. Provider code invokes `SqlExportPublication.Publish(staging,target)`, retains recoverable staging after publication failure, and polls cancellation after hashing before publication.

## Findings addressed

- S1/S2: all four visible choices map correctly. Native DROP statements cannot erase protected objects under Skip/Keep/Abort; dependent indexes/triggers and Unicode/qualified names are covered. Abort conflicts are detected before writes. Skip protects existing table/view DML; Keep preserves structures and executes the script's data statements.
- S3/S11/S12: preflight and runner use the same trigger-aware reader, preserve literal line separators and reject malformed UTF-8, raw NUL, incomplete lexical states and oversized complete statements. TEXT export uses `CAST(X'...' AS TEXT)`, retaining LF/CR/CRLF/Unicode/quotes/NUL bytes in UTF-8 databases. BLOBs use `X'...'`.
- S4/S5/S13: script and wrapper transactions are cleaned up on stop/cancel/failure; rejected COMMIT is failure and pending work is rolled back. Continued SQL errors withhold COMMIT. Every wrapper/rollback/PRAGMA result is inspected. Native AutoCommit confirms cleanup; original FK/synchronous settings are restored and re-read. The package documents SQLiteCommand.Cancel as unimplemented, so cancellation uses the documented thread-safe SQLiteConnection.Cancel.
- S6/S9: generated/hidden columns are excluded from writable projections, ordinary accessible implicit rowids are retained, WITHOUT ROWID metadata is read from table_list rather than DDL text, and AUTOINCREMENT deleted/empty high-water marks are restored for selected tables.
- S7/S8/S10/S14/S15: missing DDL, QueryError and negative counts stop publication; native source reads use a single read transaction. Comments are exported once and only for selected tables. Initial table selection is applied. Delivered SHA-256 and length describe the actual final file; the separate payload checksum footer is verified before import writes. Base64 manifest names allow whitespace and pipe characters. Existing outputs are published using the root's atomic helper.

## Verification evidence

Worker verification compiled the exact modified provider/model/lexer/log source with Roslyn into a separate scratch executable; it used System.Data.SQLite 1.0.119.0 and private raw connections, with a minimal my_sqlite/Localization shim only to isolate compilation from the product UI. Six groups use actual SQLite connections; other groups cover model/mock/preflight contracts. No SpatiaLite extension loading occurred.

Latest isolated run: 12/12 methods passed. Log: `C:/Users/johnho/Documents/mySQLPunk-reviews/2026-10-16/sqlite-repair-verify/results.log`.

New registration names (root owns registry):

- `SqliteImport_NativeConflictPolicies_ProtectDropAndDependencies`
- `SqliteImport_NativeLexer_TriggerAndExactText`
- `SqliteImport_NativeTransactions_FailureCancelAndPragmas`
- `SqliteImport_StatementLimit_MultibyteAndTerminated`
- `SqliteExport_NativeRoundtrip_GeneratedRowidSequenceTextComments`
- `SqliteExport_Failures_PreserveOutputAndRejectTamper`

Actual assertions include all four native conflict policies, protected dependent objects and shared sidecar metadata; trigger CASE/comment/semicolon grammar and firing; exact mixed newline literal text; malformed encoding and unterminated lexical input; failed tail SQL, deferred FK COMMIT rejection and provider NO for BEGIN/COMMIT/ROLLBACK; cancellation interrupt under five seconds; checked PRAGMA restoration; a private file-backed cancellation followed by a successful write from a separate connection; generated VIRTUAL/STORED columns; sparse negative/near-Int64-max rowids; deleted and empty AUTOINCREMENT history; WITHOUT ROWID with trailing DDL comments; text/BLOB/comment NUL bytes; subset sidecar and trigger scope; whitespace/pipe manifest names; external full-file hash/length; one-byte tampering; missing/source-query-error output preservation; oversized row and inaccessible rowid rejection.

A complete terminated multibyte single-line statement over 32 MiB is rejected during incremental preflight. Native export is O(n) through one sequential SQLiteDataReader per table, with size as the first ordinal and CASE guards preventing SQL hex rendering for rows beyond the safe limit. Native comment reads use the same bounded sequential pattern. The generic IDatabase fallback is bounded one-row paging; the actual native provider does not use that fallback.

`git diff --check` passed. Existing UTF-8 BOM presence and CRLF were checked against the baseline with Git checkout filters; all eight changed existing files retain their original encoding/newline convention.

The root reported a fresh integrated 15-test SQLite run passed before the final table_list metadata adjustment; root owns authoritative product Debug/Release/full-suite evidence and should rerun its focused group against that final edit.

## Explicit support/acceptance boundaries

- Native data export supports ordinary UTF-8 SQLite tables with table_xinfo/table_list metadata. Virtual modules (including FTS/RTree), inaccessible implicit rowid, unsupported metadata and non-UTF-8 database text encodings fail explicitly. Geometry BLOB bytes are preserved for ordinary tables; this is not evidence for a complete SpatiaLite/GeoPackage reconstruction.
- The safe export limit is 8 MiB of source value bytes per row/comment and 32 MiB per serialized SQL statement/DDL. Larger values stop before publication, preserving the old dump. The importer limits each SQL statement to 32 MiB and comment metadata to 1 MiB. No large-production-LOB memory/throughput benchmark was performed.
- Attach/detach, temporary-schema imports and savepoint scripts are explicitly unsupported. CTE statements under SkipExisting with pre-existing objects are rejected because their target cannot be safely classified by this importer.
- Script-supplied transactions are tracked and any active transaction is rolled back on failure/cancel. Statements already committed by an earlier explicit script COMMIT, or executed with wrapping disabled, cannot be undone; the runner does not claim whole-file atomicity for arbitrary transaction scripts.
- Row manifests establish row counts only. They do not assert all-value, spatial, production or application equivalence. Skip/Keep can intentionally yield a row-count mismatch.
- Full product build, root shared-helper fault injection, actual displayed WinForms/DPI flows, real accounts, live source databases, IIS/production and SpatiaLite/GeoPackage runtime validation remain root/user acceptance boundaries. This worker did not run a product/shared-output build.


## Scoped review correction wave

Read all five findings in `sqlite-scoped-review.md`; each was reproduced in regression coverage and corrected within SQLite-owned files.

1. Managed BEGIN now precedes the policy-generated DROP. Real SQLite tests verify both a later SQL failure and an invalid replacement CREATE restore the original populated table.
2. Native AutoCommit is reconciled at the statement-error boundary and before further statements. If OR ROLLBACK or RAISE(ROLLBACK) ends the transaction, import stops even with ContinueOnError, preventing later statements from leaking into autocommit. Actual managed/explicit transaction and trigger rollback fixtures preserve the initial table and leave no leaked table.
3. Both forms cancel every close event while busy; the closing flag only suppresses duplicate cancellation requests. Import preflight and export catalog loading honor pending close in their finally paths after clearing busy. Export catalog loading now participates in the same busy guard, protecting the owned session during its existing asynchronous catalog queries too. WinForms event tests invoke two close attempts and confirm both are cancelled, the form remains undisposed and the provider remains open; pending preflight/catalog completion then closes the form.
4. Under Skip/Keep with existing owners, sequence/sidecar DML accepts only the native exporter's exact single-owner forms. Unscoped DELETE/UPDATE, multi-owner/ambiguous inserts and broadened WHERE predicates are skipped safely. Actual SQLite tests retain deleted AUTOINCREMENT high-water marks and unrelated comments while permitting scoped restoration for newly imported owners.
5. Supplied IncludeViews/IncludeTriggers values now initialize the actual controls. StartExportAsync uses a small BuildExportOptions method so the regression can inspect the exact service options derived from those controls. A one-table database fixture with a view and a trigger referring to an omitted table verifies flags remain false and the actual export contains neither object.

Latest isolated run after this correction wave: 16/16 SQLite methods passed. It compiles the exact modified SQLite provider and wizard form sources with a minimal styling/mascot/localization shim (no production UI dependency or saved configuration); the WinForms control/event/async-finally logic is the actual modified source. Log: `C:/Users/johnho/Documents/mySQLPunk-reviews/2026-10-16/sqlite-repair-verify/review-wave-results.log`.

Additional registration names:

- `SqliteImport_ReviewRegression_ManagedDropAndImplicitRollback`
- `SqliteImport_ReviewRegression_MetadataOwnerPreservation`
- `SqliteImport_ReviewRegression_RepeatedCloseAndPreflight`
- `SqliteExport_ReviewRegression_RepeatedCloseAndInitialOptions`

Scoped diff-check passes; baseline checkout BOM/CRLF remain preserved in all eight existing changed SQLite files. No shared output build, user database, saved configuration, account access, commit or push occurred. Root still owns the fresh complete-product build/full-suite and visual DPI/real-world acceptance boundaries.
