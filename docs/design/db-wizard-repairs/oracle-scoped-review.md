# Oracle scoped repair review

Date: 2026-10-16. Decision: changes requested; six material findings remain.

Reviewed the stable Oracle repair source and `C:/Users/johnho/Documents/mySQLPunk-reviews/2026-10-16/oracle-repair-diff.patch` against Task 3 and the acceptance constraints in `plan.md`, plus `oracle-repair-report.md`. This was source/documentation review only: no build, smoke test, saved configuration, credentials, live database, or other-provider source was accessed. The worker's 12/12 Oracle and AutoReleasePolicy results are reported evidence, not independently rerun evidence. Root's final full suite remains pending.

## 1. [P1] Accepted statement variants bypass direct owner enforcement

Location: [OracleImportRunner.cs:349](C:/Users/johnho/Documents/mySQLPunk-worktrees/db-wizard-repairs/mySQLPunk/lib/OracleImport/OracleImportRunner.cs:349), through line 356; accepted forms are listed at line 326.

For selected owner `B`, `DELETE A.T WHERE ID=1` passes the statement allowlist, but `DELETE` is absent from the target-keyword set. Because Oracle permits omission of `FROM`, no token triggers validation of `A.T`. Likewise, `CREATE UNIQUE INDEX A.I ON B.T(ID)` and `CREATE BITMAP INDEX A.I ON B.T(ID)` are admitted, but `INDEX` follows `UNIQUE`/`BITMAP`, so the index owner is not checked. An adequately privileged session executes the original SQL against owner A after CURRENT_SCHEMA is pinned to B. An explicit database-link target such as `INSERT INTO B.T@REMOTE VALUES (1)` also remains admitted because `@` is never rejected; it is a direct remote target, not a dependency side effect. [Oracle DELETE syntax/examples](https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/DELETE.html), [Oracle CREATE INDEX prerequisites](https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/CREATE-INDEX.html).

Required correction: parse each supported statement's object position, including optional `FROM` and index modifiers, and reject database links under this local-owner contract. Add recording-provider regressions proving these statements fail before ALTER SESSION or any mutation. Keep same-owner accepted variants and owner-like literals/comments unchanged.

## 2. [P1] Quoted spatial metadata targets bypass the SESSION_USER and option gates

Location: [OracleImportRunner.cs:296](C:/Users/johnho/Documents/mySQLPunk-worktrees/db-wizard-repairs/mySQLPunk/lib/OracleImport/OracleImportRunner.cs:296); callers are lines 74-80 and 95-99.

`IsSpatialMetadataMutation` only matches unquoted `INSERT INTO`, `DELETE FROM`, or `UPDATE USER_SDO_GEOM_METADATA`. Valid `INSERT INTO "USER_SDO_GEOM_METADATA" (...) VALUES (...)` and `DELETE "USER_SDO_GEOM_METADATA" WHERE ...` are therefore ordinary accepted statements to the runner. With selected owner B and connected session user SCOTT, they do not request the SESSION_USER check and can mutate SCOTT's USER metadata view. With AutoRegisterSpatialMetadata=false, they still execute instead of being skipped. The newly added option test covers only the plain unquoted spelling.

Required correction: derive metadata mutation classification from the same parsed target identity as owner validation, recognizing quoted uppercase identifiers and optional FROM. Apply both gates to all admitted spellings, or reject unsupported spellings before execution. Add separate regressions for the quoted spelling, optional FROM, session-owner mismatch and disabled registration.

## 3. [P1] TruncateAndInsert misses generated large-LOB rows and can delete imported data

Location: [OracleImportRunner.cs:284](C:/Users/johnho/Documents/mySQLPunk-worktrees/db-wizard-repairs/mySQLPunk/lib/OracleImport/OracleImportRunner.cs:284), through line 289; exporter emits the accepted blocks at [OracleSqlExportService.cs:543](C:/Users/johnho/Documents/mySQLPunk-worktrees/db-wizard-repairs/mySQLPunk/lib/OracleExport/OracleSqlExportService.cs:543).

The conflict strategy recognizes only statements beginning `INSERT INTO`. Supported large BLOB/CLOB/WKB rows begin `DECLARE`, so an all-large-LOB DataOnly dump never truncates an existing table. Worse, a generated LOB block followed by a small ordinary INSERT first appends the LOB row, then truncates the table immediately before the second row, deleting the row just imported. With no manifest, this can return Success=true. A manifest can detect the final count mismatch, but it does not prevent the destructive ordering.

Required correction: extract the single INSERT target from validated generated LOB blocks and apply the per-table truncate policy before their first execution. Normalize table identity so qualified/unqualified equivalent targets share one truncate record. Regress all-LOB and LOB-first/mixed-row DataOnly scripts, including no-manifest success/count behavior.

## 4. [P1] Supported binary floating-point values lose precision in emitted literals

Location: [OracleSqlExportService.cs:637](C:/Users/johnho/Documents/mySQLPunk-worktrees/db-wizard-repairs/mySQLPunk/lib/OracleExport/OracleSqlExportService.cs:637), through line 638; BINARY_FLOAT/BINARY_DOUBLE are explicitly accepted at line 441.

Finite float/double values use `Convert.ToString(value, InvariantCulture)`. On this .NET Framework target that uses general formatting with too few significant digits for guaranteed round trips. For example, a double such as 1.2345678901234567 is emitted with only 15 significant digits. Export can publish successfully and manifest row counts cannot detect the changed value. [Microsoft Double formatting documentation](https://learn.microsoft.com/en-us/dotnet/api/system.double.tostring) recommends an explicit precision sufficient for round trips.

Required correction: use type-aware G9/G17 formatting and Oracle BINARY_FLOAT/BINARY_DOUBLE literal typing (`f`/`d` suffixes or an equivalently exact native codec); untyped large exponents can otherwise be interpreted as NUMBER and overflow. Preserve signed zero explicitly or reject it with a stated unsupported gate until verified. Add formatter/recording-reader cases that assert exact emitted tokens and round-trip values, including a finite high-exponent double. Real Oracle storage equivalence remains pending.

## 5. [P2] Native view DDL is exported but rejected by the import prepass

Location: [OracleImportRunner.cs:326](C:/Users/johnho/Documents/mySQLPunk-worktrees/db-wizard-repairs/mySQLPunk/lib/OracleImport/OracleImportRunner.cs:326); native export route is [OracleSqlExportService.cs:271](C:/Users/johnho/Documents/mySQLPunk-worktrees/db-wizard-repairs/mySQLPunk/lib/OracleExport/OracleSqlExportService.cs:271), through line 273.

The exporter writes DBMS_METADATA view DDL unchanged, while the allowlist accepts only `CREATE VIEW`. Native forms such as `CREATE OR REPLACE FORCE EDITIONABLE VIEW "SCOTT"."V" AS SELECT ...` fail the complete prepass before execution even with a correct owner and bundled tables. IncludeViews remains an advertised/default option. The recording fixture returns no view DDL, so current successful tests do not cover this route. This is a source-level producer/consumer mismatch; no claim is made that every server version's view DDL was exercised.

Required correction: support the bounded native view modifiers while checking the actual view owner and referenced owners, or explicitly reject views at export before publication until that grammar is supported. Add a representative native view DDL fixture through exporter and runner, plus a cross-owner variant that still fails before execution.

## 6. [P2] Disabled FK definitions are omitted from schema dependency ordering/gates

Location: [OracleSqlExportService.cs:323](C:/Users/johnho/Documents/mySQLPunk-worktrees/db-wizard-repairs/mySQLPunk/lib/OracleExport/OracleSqlExportService.cs:323); native table DDL is fetched unchanged at line 649.

The dependency query filters `c.STATUS='ENABLED'`, while native table DDL also retains disabled FK definitions. Consequently a disabled FK to an unselected/cross-owner parent escapes the documented rejection, and a selected child can be emitted before its disabled-FK parent. Disabling enforcement does not remove the schema dependency: Oracle still requires the referenced primary/unique key to be defined. A successful export can therefore publish a schema dump that cannot be restored under the selected-owner/parent-first contract. [Oracle foreign-key restrictions and disabled constraints](https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/constraint.html).

Required correction: include all FK definitions for schema ordering and parent/owner gates; data-only enforcement may be handled separately if needed. Add disabled-FK fixtures for parent-first ordering, missing/cross-owner parent and cycle rejection, checking preservation of the previous output on refusal.

## Other scoped conclusions and limits

The original returned-NO false-success path is addressed by checked execution responses. Source READ ONLY is confined to the dedicated source transaction, source cleanup precedes publication, rows use a sequential reader, explicit row/SQL/LOB limits stop publication, and unsupported identity/type/geometry cases have explicit gates. Actual per-value SRID and spatial metadata are emitted rather than fabricated defaults. Selection tags/initial options and close-after-worker-cleanup paths are present in both forms. No additional material finding was identified in those inspected paths.

These conclusions are static review, not native-server acceptance. Oracle LOB execution, geometry/DIMINFO order, native provider numeric/timestamp behavior, snapshot concurrency, server-version compatibility, cancellation latency and interactive forms remain unverified. Shared owned-session/publication helpers and other providers are root/other reviewers' scope. Stop this review here and correct the six bounded issues in one wave before final verification.

## Scoped re-review after the six-finding correction wave

Date: 2026-10-16. Decision: the six findings are addressed in the frozen source. Specification and static code-quality review approved for this bounded correction wave; no remaining material finding or direct correction regression was identified in this scope.

Reviewed package: `C:/Users/johnho/Documents/mySQLPunk-reviews/2026-10-16/oracle-corrections-final-diff.patch`, current frozen Oracle source, and the three added regression definitions. This is the requested single scoped re-review, including the final ONLY-wrapper guard and joined-view ON correction. No build/test, saved configuration, live database, other-provider source or subagent was used by this reviewer.

| Original finding | Status | Source and regression evidence inspected |
| --- | --- | --- |
| 1. Direct owner enforcement variants | Addressed | OracleImportRunner.cs lines 314-365 parse DELETE with optional FROM, modified CREATE INDEX owner positions and canonical targets; lines 378-387 reject database-link tokens and cross-owner targets. Line 316 explicitly rejects unquoted ONLY wrappers instead of treating them as object names. OracleImport_ScopedTargetVariantsMetadataAndLobTruncate includes zero-execution DELETE/UPDATE ONLY cases, original cross-owner/index/link cases, and accepted same-owner/literal cases. |
| 2. Spatial metadata spelling gates | Addressed | OracleImportRunner.cs lines 300-304 classify the parsed mutation identity; existing preflight SESSION_USER and disabled-registration paths consume that classification. The import regression covers quoted INSERT/UPDATE, DELETE with optional FROM, wrong/matching SESSION_USER, and disabled registration without a metadata mutation or USER-gate query. |
| 3. Generated LOB truncate ordering | Addressed | OracleImportRunner.cs lines 282-293 use the one parsed INSERT target from generated DECLARE blocks and canonical owner-qualified identity before execution. Lines 327-353 reject multiple INSERT targets. No-manifest recording cases for all-large and LOB-first/mixed rows verify one truncate before the first LOB and two final rows from nine initial rows, including qualified/unqualified equivalent targets. |
| 4. Binary float/double literal fidelity | Addressed within the stated bound | OracleSqlExportService.cs lines 635-650 emit G9f/G17d tokens, reject non-finite values and explicitly reject negative zero before publication. OracleExport_ScopedBinaryFloatRoundTrip checks local bit equality after parsing, high exponents/subnormals, the recording-reader pipeline, and preservation of previous output on negative-zero rejection. The reader wrapper preserves the negative-zero bit pattern that DataTable storage would normalize. Native Oracle storage equality remains unverified. |
| 5. Native view grammar | Addressed | OracleImportRunner.cs lines 356-365 and 402 accept the bounded native OR REPLACE/FORCE/EDITIONABLE view prefix while retaining actual owner checks. Lines 426-433 apply ON object-position checking only to CREATE INDEX, so joined view alias predicates do not become owner targets. OracleExport_ScopedNativeViewsAndDisabledForeignKeys covers exporter-to-runner native view DDL, same-owner joined views, changed view owners, and changed FROM/JOIN owners with zero mutation. |
| 6. Disabled FK schema dependencies | Addressed | OracleSqlExportService.cs line 323 includes all FK definitions for schema-bearing modes and retains the enabled-only filter for DataOnly. The scoped view/FK regression covers disabled parent-before-child ordering and refusal of missing, cross-owner and cyclic disabled references while preserving existing output. |

The fixes preserve the accepted architecture and fail explicitly for the added unsupported ONLY and signed-zero boundaries. The review does not expand supported Oracle types, server versions, PL/SQL, or account behavior. No additional review loop is requested.

Execution evidence is owned by root/implementer. At this review handoff, the implementer reported 15 Oracle groups plus AutoReleasePolicy passing before the last ONLY guard; the final 15-group run including ONLY was still running. This reviewer did not rerun or independently certify those results. Root must record the final targeted/full-suite and Debug/Release outputs. All previously listed real Oracle, native storage/snapshot/version/cancellation/UI boundaries remain pending.
