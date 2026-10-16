# MySQL wizard repair report

Baseline: d41825cead810bc67f263753bb5f986a7f69c65c. Review input: C:/Users/johnho/Documents/mySQLPunk-reviews/2026-10-16/mysql-review.md. User-authorized provider repair; no live database, saved connection, SSH tunnel or production test was performed.

## Changes

- `lib/MySqlImport/MySqlScriptReader.cs` is the shared preflight/execution lexer. It preserves original literal characters/newlines and ordinary comment text, splits multiple statements per line, recognizes DELIMITER only outside SQL text, and rejects unterminated syntax, executable MySQL/MariaDB version comments, embedded NUL, excessive tokens and statements beyond 32 MiB UTF-8. Executable version comments require the native client path; they are not silently dropped.
- `MySqlImportRunner.cs` redirects comment-prefixed USE and recognized schema-qualified object positions even without prior USE. Identifier token replacement preserves string/JSON/comment content and longer identifiers. Ambiguous qualifiers and dynamic execution/CALL/client commands fail explicitly; database-wide destructive commands fail rather than writing outside the chosen target. This is a conservative logical-dump restore boundary, not an arbitrary SQL sandbox.
- Initial target-table catalog is separate from the live object cache. SkipExisting protects initial objects while fresh CREATE plus repeated INSERT batches execute. Comment-prefixed DROP cannot bypass protected strategies; synthetic DROP checks actual status=NO results. Session initialization and cleanup validate execution results. Uncommitted explicit transactions, failed COMMIT, and failed cleanup cannot produce Completed. Logs auto-flush each statement. Cancellation is checked after returned SQL, including the final statement.
- `MySqlImportPreflightScanner.cs` uses the same parser and verifies the native SHA-256 footer against the exact byte prefix. The footer lookup operates on raw bytes, including non-ASCII input near the final buffer boundary.
- `MySqlSqlExportService.cs` validates snapshot setup, requires verified InnoDB selected tables for SingleTransaction, uses UTC session/output time zone, validates snapshot rollback before publication, and never commits the source session. Missing table DDL/metadata/bounds/pages or source row-count discrepancies fail explicitly. Generated columns retain DDL but are omitted from INSERT columns and values. Ordinary 2D spatial values now use bounded WKB/SRID export (see [spatial follow-up](mysql-geometry-report.md)); unsupported runtime value types fail before publication. TIME microseconds are preserved.
- Export has a 1 MiB source-row limit, at most 16 rows per source page, and an 8 MiB UTF-8 INSERT limit. Server-side OCTET_LENGTH preflight and CASE-projected pages prevent oversized LOB materialization, including an enlargement after the initial bound query when SingleTransaction is disabled. Oversized values fail explicitly; no all-size/streaming-LOB claim is made.
- Export uses the shared `SqlExportPublication.Publish` helper. Replacement failure keeps the completed staging file and reports its path; failure/cancellation before publication preserves the previous destination. Hashing is cancellable and final byte count includes the footer.
- `MySqlExportWizardForm.cs` applies supplied initial mode, table selection and option flags. Catalog loading remains busy until awaited work finishes; close requests wait for cleanup. Both forms restore the cancel button and dispose per-operation cancellation sources after awaited completion. Shared Form1 owned-session routing is integrated by the root implementer.

## Changed files and registration

Provider-owned changes: `mySQLPunk/lib/MySqlImport/{MySqlScriptReader,MySqlImportRunner,MySqlImportPreflightScanner,MySqlImportLog}.cs`, `mySQLPunk/lib/MySqlExport/MySqlSqlExportService.cs`, both MySQL wizard forms, `tests/SmokeTests.MySqlImport.cs`, `tests/SmokeTests.MySqlExport.cs`.

New Compile entry: `lib\MySqlImport\MySqlScriptReader.cs` (root-owned project integration).

New test registrations:

- MySqlImport_Regression_TargetLiteralsAndStrategies
- MySqlImport_Regression_CancelTransactionsAndPreflight
- MySqlExport_Regression_GeneratedBoundsAndFailures

Existing touched files retain their original UTF-8 BOM state and CRLF endings (byte-checked against HEAD). The new lexer uses UTF-8 without BOM and CRLF.

## Verification evidence

An isolated Roslyn C#/.NET Framework harness compiled the changed services/models, shared publication helper and both existing smoke files against the pinned baseline executable's IDatabase interface. It ran all 11 original/new MySQL test methods successfully; the root owns full application builds and the shared smoke suite.

Harness: `C:/Users/johnho/Documents/mySQLPunk-reviews/2026-10-16/mysql-repair-harness`.

Durable coverage includes comment-prefixed/inline USE, same-line splitting, explicit qualified INSERT without USE, exact multiline/JSON/escaped literal and comment preservation, longer identifier boundaries, mixed new/existing SkipExisting, protected commented DROP and database-wide DROP rejection, last-command cancellation, uncommitted script/failed COMMIT/failed cleanup, executable comments/unterminated literals/digest mismatch, generated INSERT omission, round-trip preflight/digest compatibility, failed snapshot/rollback/non-InnoDB/large-row handling, concurrent enlargement marker, cancelled final source page, and Windows replacement-lock failure with both old output and complete verified staging preserved.

## Limits and unverified acceptance

- No MySQL/MariaDB server round trip, two-database source-preservation fixture, actual generated-column insertion, SQL CASE type inference, zero dates, server max_allowed_packet, real permissions, active editor transaction or live SSH disposal was tested. Recording provider SQL/status evidence is distinct from server acceptance.
- IDatabase exposes no cancellable active command. Stop is observed when the current command/read returns or times out; it does not promise immediate server command cancellation. The forms keep the owned job alive while awaiting cleanup.
- Imports may have applied earlier autocommitted DDL/DML before a later rejected statement; failure logs and counters remain authoritative and no all-or-nothing restore claim is made. Explicit uncommitted transactions are rolled back and reported failed.
- Concurrent source DDL and source pagination without a snapshot remain outside a consistency guarantee. SingleTransaction is restricted to verified InnoDB tables; disabling it permits ordinary non-snapshot reads, with bounded pages and row-count discrepancy detection.
- Executable version comments, dynamic SQL/CALL, ambiguous dotted identifiers, noncanonical SQL_MODE changes, AUTOCOMMIT changes, complex transaction-control modifiers, cross-engine geographic SRS migration, oversized source rows, and unknown runtime value types require the native-client path. Logical import is intentionally conservative about unsupported constructs.
- Full supported-type fidelity remains pending real-server fixtures; in particular timestamp/zero-date/provider type mapping and MariaDB variants are not certified by these recording tests. Shared owned-session and routing/publication test results belong to the root report.

## Scoped review correction wave

The focused MySQL reviewer identified two provider gaps after the initial repair. They are corrected in `MySqlImportRunner.cs` with one additional durable test method in `tests/SmokeTests.MySqlImport.cs`.

- INSERT/REPLACE target extraction now uses lexical tokens, covers LOW_PRIORITY/HIGH_PRIORITY/DELAYED/IGNORE and optional INTO, and is shared by target redirection and SkipExisting. Protected initial-table writes cannot bypass the policy using these accepted forms. Unidentifiable INSERT/REPLACE targets fail explicitly under SkipExisting.
- NEW/OLD pseudo-row aliases are accepted only inside a recognized CREATE TRIGGER definition. Trigger ON object positions are redirected to the selected target; ordinary non-trigger NEW/OLD qualifiers retain the conservative rejection behavior. Literals containing NEW/OLD or source schema text remain unchanged.

New root registration: `MySqlImport_Regression_ModifiedInsertProtectionAndTriggerPseudoRows`.

Fresh isolated compilation and all 12 MySQL provider smoke methods pass. The new test exercises eight INSERT/REPLACE forms against protected old and fresh tables, repeats all forms in a data-only fixture, covers quoted/qualified targets without USE, then imports one native DELIMITER trigger containing NEW/OLD, real database-qualified objects and source-looking literals. It also verifies the pseudo-row exemption does not apply outside trigger DDL. BOM/CRLF rechecked against HEAD and provider diff whitespace checks remain clean. No real database or shared full build was run by this worker. The third reviewer finding about single-table related-object defaults is assigned to the root implementer.
