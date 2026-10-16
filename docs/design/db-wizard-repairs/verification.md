# Wizard repairs: verification and support boundaries

Baseline: `d41825cead810bc67f263753bb5f986a7f69c65c`. Local repair branch: `codex/db-wizard-repairs`. The three feature branches are preserved. Their stack also includes PostgreSQL; this repair does not certify that provider.

## What changed

- MySQL: one bounded lexical reader for preflight/execution; rewrite identifiers without changing literal/comment bytes; enforce the chosen target; separate pre-existing objects from newly created tables; protect conflict strategies; project generated columns correctly; validate source snapshot/cleanup; preserve initial table/mode and await worker cleanup.
- SQLite: bind strategy values rather than cast indexes; protect native DROP/dependencies; parse trigger bodies and exact UTF-8 text; check all transaction results and actual native transaction state; restore PRAGMAs; preserve generated columns, implicit rowid, sqlite_sequence, NUL/multiline/BLOB bytes and comment sidecars. Native exports use one sequential reader with bounded SQL-side projection.
- Oracle: check provider NO results, owner/transaction/cleanup, parser bytes/comments/alternative quotes and UI selection. Native source snapshot and sequential readers replace full-table DataTable loading. Spatial uses original SRID/DIMINFO/tolerance and option gates. BLOB/CLOB/large WKB use bounded temporary-LOB PL/SQL blocks rather than one oversized literal.
- Common: the six native wizard entries open an owned provider session with its own SSH transport lifetime, never borrowing the editor connection. MySQL/Oracle wizard sessions disable pooling. Export publication uses same-directory File.Replace or first-file File.Move, retaining old destination and complete staging when publication fails.

## Current deliberate limits

| Provider | Limit / unsupported cases |
| --- | --- |
| MySQL | Source row <=1 MiB; 16-row source pages; INSERT <=8 MiB. Snapshot requires verified InnoDB. Executable version comments, dynamic SQL/CALL, ambiguous scope and native spatial values are explicitly unsupported. Bigger LOB chunking remains future work. |
| SQLite | Source row <=8 MiB; strict UTF-8 SQL and <=32 MiB decoded statement. Virtual modules and inaccessible row identities fail explicitly. SpatiaLite/GeoPackage extension-dependent acceptance is pending. |
| Oracle | Row values <=8 MiB, generated statement <=28 MiB, input statement <=32 MiB. BLOB chunks <=2000 bytes. Native export currently requires AL32UTF8; identity data, signed zero, LRS/curved/compound spatial cases and unverified types fail explicitly. Cross-owner targets and general stored/dynamic PL/SQL are rejected; generated bounded LOB and DROP wrappers are supported. |

Unsupported data aborts the job before final publication; no truncation, fabricated SRID or silent approximation is used. These are finite budgets, not claims to cover theoretical provider type maxima. Counts/manifest success alone do not prove value or schema fidelity.

## Evidence

- Original baseline: full smoke 268 groups plus AutoReleasePolicy passed.
- Common regression: actual locked-file publication failure preserves both original/candidate; actual private SQLite owned-session disposal rolls back job data and leaves editor connection open; both MySQL table entry handlers preserve raw identity and DataOnly.
- MySQL/Oracle use recording-provider contracts including status=NO, rather than live engines. SQLite uses tiny private actual System.Data.SQLite fixtures; no saved user databases/dumps or account settings were read.
- Provider reports: [MySQL](mysql-repair-report.md), [SQLite](sqlite-repair-report.md), [Oracle](oracle-repair-report.md).
- Final actual-product gate: **294 smoke groups and AutoReleasePolicy passed**, followed by successful explicit **Debug and Release** application builds. Optional live groups printed SKIP; no live DB acceptance is implied.
- Final focused evidence: actual-product SQLite19 and Oracle15 passed. MySQL correction harness12 passed and the final full suite executed all registered MySQL regressions. Common four groups passed within the final full run.
- Independent scoped code/spec reviews and one bounded correction re-review per provider approved: [MySQL](mysql-scoped-review.md), [SQLite](sqlite-scoped-review.md), [Oracle](oracle-scoped-review.md). Original findings remain in those reports as history; appended/final verdicts supersede initial changes-requested decisions.
- Logs: repair-full-smoke-final.log, repair-debug-final.log, repair-release-final.log, repair-sqlite-final-tests.log and repair-oracle-scoped-smoke.log in the local evidence directory.
- Source and test files were frozen before this gate; only documentation/history was finalized afterward. Existing-file encoding/BOM/CRLF audit and git diff --check passed.

To repeat: run `tests/Run-SmokeTests.ps1` from this checkout using Windows PowerShell/PowerShell and installed MSBuild. Optional `MYSQLPUNK_SMOKE_FILTER` values: `Wizard repairs`, `MySQL`, `SQLite`, `Oracle`. Do not run quarantined ReportExportTests.

Local baseline, red reproductions, focused test logs and final build logs are retained outside Git at `C:/Users/johnho/Documents/mySQLPunk-reviews/2026-10-16/`.

## Pending acceptance

Real MySQL/MariaDB and Oracle/Spatial roundtrips, live SSH/permissions/source-editor concurrency, multi-GiB memory/performance, native UI/DPI/audio and production are not verified. MySQL stops after the current command returns/times out; immediate server-command cancellation is not promised. A failed import can retain earlier committed DDL/DML; no automatic cleanup/retry or all-or-nothing restore guarantee is introduced.

Oracle reference semantics used during review: [ODP.NET transactions](https://docs.oracle.com/en/database/oracle/oracle-database/19/odpnt/featOraCommand.html), [SET TRANSACTION](https://docs.oracle.com/en/database/oracle/oracle-database/21/sqlrf/SET-TRANSACTION.html), [SDO_UTIL WKB and SRID](https://docs.oracle.com/en/database/oracle/oracle-database/19/spatl/SDO_UTIL-reference.html). These references are design evidence, not a live acceptance result.
