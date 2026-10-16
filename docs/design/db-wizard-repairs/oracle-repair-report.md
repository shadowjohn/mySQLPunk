# Oracle wizard repair report

Scope: Task 3 against d41825c. Oracle sources/forms and existing Oracle smoke files only. Root owns shared session/publication helpers, Form1, project/test registration and final full builds. No Oracle server, external database, credentials, saved connection files or account/session state was accessed. No commit or push by this worker.

## Execution repairs

Every recording-provider ExecSQL response requires status=OK and retains status=NO reasons. Statement, DROP, TRUNCATE, transaction start, COMMIT and ROLLBACK failures cannot report success. KeepExisting only skips recognized existing-object errors on CREATE/ALTER.

Native my_oracle uses OracleTransaction and cancellable OracleCommand. Error/cancellation cleans pending DML, including script-created transactions with the UI option unchecked. COMMIT failure fails the operation and rollback failure stays in the result. Oracle DDL/TRUNCATE implicit commits are explicit: whole-file rollback is not promised.

A complete parser/target prepass precedes execution. CURRENT_SCHEMA is pinned to the selected owner. Cross-owner object targets, session/privilege changes, unsupported dynamic SQL and ambiguous stored/anonymous PL/SQL reject before execution. The checked canonical DROP wrapper and generated temporary-LOB INSERT blocks are supported. Literals/comments are never remapped. Manifests use owner-qualified identities and count the same owner. CURRENT_SCHEMA is not a security sandbox for arbitrary SQL/dependency side effects.

USER_SDO_GEOM_METADATA restore requires SESSION_USER equal to selected owner; CURRENT_SCHEMA cannot redirect that USER view. Disabling registration skips script metadata mutations and never invents missing metadata. Both forms wait for worker cleanup before close, including catalog reads/file preflight. Root supplies isolated owned sessions.

## Parser and logging

One bounded parser serves preflight and execution: ordinary/alternative quotes, quoted identifiers, comments, ordinary SQL semicolon removal and slash-terminated PL/SQL retaining its final semicolon. It records source-encoding byte counts and original start lines, rejects NUL/unterminated syntax and limits statements/physical lines to 32 MiB. Comments become whitespace; source bytes remain counted. Text preflight does not validate server syntax/permissions.

Logs use Windows-safe owner text, GUID names, CreateNew and AutoFlush. Concurrent jobs do not overwrite second-resolution filenames.

## Export behavior and exact limits

- READ ONLY executes on the dedicated source transaction before reads and rolls back on success/failure/cancel. Restore SQL contains no source READ ONLY. Native SYS snapshot sessions reject explicitly.
- Native table rows use OracleCommand.ExecuteReader(CommandBehavior.SequentialAccess); no full-table DataTable or SELECT-star fallback exists. Catalog/dictionary metadata is materialized separately. Recording providers expose a forward-reader contract.
- Total row values are bounded to 8 MiB; emitted row SQL/PLSQL to 28 MiB. BLOB/WKB uses temporary BLOBs and <=2000-byte HEXTORAW chunks. Larger CLOBs use temporary CLOBs with small escaped text chunks. Newlines survive; temporary LOBs free on success and exception.
- Small WKB/BLOB uses explicit TO_BLOB. EMPTY_BLOB/EMPTY_CLOB remains distinct from NULL. Scalar hex text <=4000 bytes and escaped UTF-8 scalar text <=4000 bytes. RAW >2000 bytes, oversized non-CLOB text, NUL or over-budget values fail before publication. Native text sources explicitly require AL32UTF8. NCLOB and unverified codecs reject.
- Geometry preserves each actual SRID, including NULL, via FROM_WKBGEOMETRY's second argument. Native preflight rejects LRS, unsupported dimensions/types, curved/compound element types and non-linear interpretations. Supported WKB semantics do not claim every Oracle internal SDO representation is preserved.
- Actual ALL_SDO_GEOM_METADATA/DIMINFO supplies dimension names, bounds, tolerances, NULL DIMINFO/SRID and metadata absence. No 4326, invented bounds/tolerances or fabricated SIDX names remain. Original spatial index DDL emits once after metadata/data.
- Native table/view/index DDL comes from DBMS_METADATA instead of the inherited copy type mapper. Bounded native CREATE OR REPLACE / FORCE or NOFORCE / EDITIONABLE or NONEDITIONABLE VIEW grammar is accepted, with actual view/reference owners checked. Independent comment/regular-index/spatial-index option gates apply.
- Virtual/hidden columns are omitted. Native column queries avoid the inherited collation fallback losing generated flags. Identity data rejects until sequence/state restoration is verified; SchemaOnly remains subject to dependencies. Timestamp precision above .NET ticks, timezone timestamps, unsupported user types, non-finite numerics and signed-zero binary floats reject explicitly. Finite BINARY_FLOAT/BINARY_DOUBLE use G9f/G17d typed literals.
- Schema FK parents are ordered first using every FK definition, including disabled definitions. Missing/unselected parents, cross-owner references and selected cycles/self references reject before publication. DataOnly considers enabled FKs only. Native schema export rejects recorded external sequence/function/package/type dependencies, which this wizard does not bundle. This is a bounded logical table export, not a universal Oracle backup.
- Checkbox edits update tagged selections, filters retain them, initial owner-qualified selection/options apply, and StartExport passes qualified names. DataOnly excludes views.
- Unique staging uses root's SqlExportPublication.Publish. The old target is never deleted first. Publication failure keeps staging; earlier failure removes staging only.

Restrictions are visible in the options UI and dump header, not just implementation comments.

## Local evidence

`MYSQLPUNK_SMOKE_FILTER=Oracle ./tests/Run-SmokeTests.ps1`: **15/15 Oracle groups and AutoReleasePolicy passed after the scoped correction wave**, including product compilation. Evidence: `C:/Users/johnho/Documents/mySQLPunk-reviews/2026-10-16/repair-oracle-scoped-smoke.log`. Root runs the final full suite and Debug/Release builds.

New durable registrations supplied to root:

- OracleImport_ReturnedNoAndTransactionControls
- OracleImport_TargetOwnerPreflightAndCancellation
- OracleImport_ParserBytesCommentsAndAlternativeQuotes
- OracleExport_SpatialMetadataAndOptionGates
- OracleExport_BoundedLobsSnapshotFailureAndCancel

Coverage: returned-NO statement/commit/drop/truncate/rollback; transaction option and cancellation; owner ON/REFERENCES/COLUMN validation; comments, alternative quotes, UTF-8/CRLF bytes; source snapshot commands/cleanup; actual projected 3D metadata and per-value projected/NULL SRIDs; all DDL option gates; virtual projection and identity rejection; 3KB WKB/5000 Chinese-character CLOB chunks; empty LOBs; first flushed output while reader is active; >8MiB failure preserving old output; FK cycle/missing parent rejection; and actual exporter LOB script passed through the real runner with a recording provider.

Nine existing Oracle files retain baseline BOM and uniform checkout CRLF (Git stores normalized LF). Scoped git diff --check passes. The unchecked spatial-registration and script-created transaction cleanup assertions are included in the passing Oracle run.

## Pending acceptance

No Oracle server roundtrip, concurrent-update snapshot, native geometry/DIMINFO order, PL/SQL LOB execution, native FK/default dependency/version compatibility, two-owner account behavior, cancellation latency, memory stress or interactive form acceptance was run. Recording fixtures prove control flow and emitted limits, not storage equivalence. Dependencies invisible to the account's Oracle dictionary remain outside the contract. Native-fidelity/server-version acceptance is pending; browser/IIS/production are outside this desktop repair.


## Scoped correction wave (six review findings)

The six material findings in oracle-scoped-review.md were corrected together. The existing architecture and other providers were not changed.

1. Direct DML targets share a token-derived identity, recognizing DELETE with optional FROM. CREATE UNIQUE/BITMAP INDEX owner positions are parsed independently of modifiers. Database-link tokens reject before session/mutation. Unquoted ONLY target wrappers explicitly reject rather than being mistaken for local object names. Same-owner variants and owner/@ text inside literals/comments remain safe.
2. Spatial metadata classification uses that same parsed identity, including quoted uppercase USER_SDO_GEOM_METADATA and optional FROM. Both SESSION_USER checking and disabled-registration skipping apply to all admitted target spellings.
3. A validated generated DECLARE LOB block has exactly one INSERT target. TruncateAndInsert uses it before execution, and qualified/unqualified identities share one canonical truncate record. Recording regressions start with nine rows and end with exactly two for both all-large and LOB-first/mixed dumps without manifests.
4. Finite binary floats emit G9 with f suffix and finite binary doubles emit G17 with d suffix, including high exponents and subnormals. .NET bit-level parsing regressions verify tokens roundtrip locally. Signed zero explicitly rejects before publication until native bit-preserving storage is verified. The recording reader preserves the negative-zero bit pattern (DataTable storage alone normalizes it).
5. Native FORCE/EDITIONABLE view forms pass through the actual exporter and runner. ON owner-position checking applies to CREATE INDEX object targets, not view JOIN alias predicates. Joined same-owner views pass; changed view/reference owners reject before any mutation.
6. Schema dependency ordering includes disabled FK definitions. Disabled parent-before-child, missing parent, cross-owner and cycle fixtures exercise the gates and preservation of prior output. Data-only retains its enabled-FK enforcement filter.

Additional durable registrations supplied to root:

- OracleImport_ScopedTargetVariantsMetadataAndLobTruncate
- OracleExport_ScopedBinaryFloatRoundTrip
- OracleExport_ScopedNativeViewsAndDisabledForeignKeys

Final scoped evidence is the 15-group Oracle-filter run in repair-oracle-scoped-smoke.log plus scoped diff whitespace/BOM/CRLF audits. Root owns the final independent scoped review, full suite and Debug/Release builds. All previously listed native-server/storage/version/cancellation/UI acceptance boundaries remain pending.
