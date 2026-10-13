# Production preflight compatibility investigation (2026-10-13)

Base: merged master f0d4f56. Active worktree: .worktrees/mssql-export, branch codex/mssql-export-preflight-compat. Original master and unrelated Desktop Properties files remain untouched.

## Confirmed diagnosis

The production screenshot is blocked at preflight, before any data export. It does not show corrupted data or an export runtime failure.

- Data modes require snapshot_isolation_state=1. The old UI collapses all other states to OFF, losing transition/unknown distinctions. The user reaffirmed preserving consistency and having a DBA assess ALLOW_SNAPSHOT_ISOLATION.
- Permission failures lose their useful reason: EnsureVisibility throws for missing VIEW DEFINITION, the service replaces it with a generic exception type, and the wizard replaces that with a generic retry message. User explicitly requires insufficient-permission handling.
- The original reader rejected table non-MS_Description extended properties and all view properties, and aggregated the view error with indexes/triggers. Returned production metadata confirms the affected objects only need nvarchar/int properties; indexes/triggers are zero.
- Any dependency with a database qualifier is rejected, including same-database Transfer.dbo.*. Merely allowing it is unsafe: unchanged view bodies would point back to the source when restored into a differently named database.

## Immediate bounded correction

1. Preserve a typed, safe prerequisite diagnostic across metadata -> service -> wizard for missing database VIEW DEFINITION and known SQL permission/login/database-access denials. Use a fixed allowlist of error numbers/categories and fixed user-facing wording; never display raw provider messages/connection strings/inner exceptions. Unknown errors retain generic handling.
2. Clearly separate SELECT, readable definitions, connection access and DBA-only configuration remediation. Do not require sysadmin/db_owner or attempt ALTER/GRANT/login changes. ALTER permission is not an exporter prerequisite when snapshot is already ON.
3. Keep data export blocked unless SNAPSHOT is ON. Distinguish OFF, ON, transitions and unknown states; give DBA/retry/schema-only guidance. Never downgrade to READ COMMITTED/NOLOCK or silently omit rows/objects.
4. Add a native confirmation-page recheck action preserving tables/options/path; remain cancelable and do not enable Start from stale preflight. If a catalog read fails, provide an in-place retry without duplicate loads or stale selection expansion. Reuse existing wizard lifecycle and generation logic.
5. Write meaningful RED/GREEN regressions around safe diagnostic propagation, unknown-error redaction, both wizard failure surfaces, snapshot-state/mode matrix and retry lifecycle. Run affected smoke groups and Debug/Release sequentially. No database/production operations or ReportExportTests executable.

## Evidence collection before schema compatibility changes

[diagnose-preflight.sql](diagnose-preflight.sql) is read-only and should run under the same production login/database. It reports version, effective permissions, snapshot state, affected property names/types (not values), view feature counts/names and dependency identities. Without VIEW DEFINITION it explicitly stops instead of treating hidden objects as absent.

The returned production results below determine the extended-property scope. Supporting same-database qualified definitions requires catalog-correct name resolution plus safe SQL identifier rewriting and an actual differently named target restore; not a predicate-only relaxation. These schema compatibility changes are implemented separately from the bounded diagnostic correction.

## Verification boundaries

Local tests and authorized LocalDB metadata-query syntax checks are not production export/restore acceptance. No production DB setting, grant, data, source definitions, merge, push or release is authorized by this diagnostic task. Existing dedicated fixture authorization remains available for a later scoped restore test; do not substitute another instance.

## Diagnostic collection progress

- The full metadata batch was syntax/execution checked on the exact previously authorized SQL2025 LocalDB instance, master catalog only. No application rows, source definitions, users, grants or settings were changed. Missing SELECT on sys.sql_expression_dependencies is now explicitly reported; lack of visibility is never an empty-catalog assertion.
- The user's initial `OK | 177 ms` is execution status only. The pasted attachment contained the SQL text, not result rows. QueryForm.cs classifies by first keyword; the full diagnostic starts with SET and therefore routes through ExecuteNonQuery. MySqlPunk's current MSSQL reader also exposes one result table. Use SSMS/sqlcmd for the full batch, or execute SELECT-only diagnostic statements individually in mySQLPunk. This existing query-editor limitation is not part of the exporter fix.
- Provided a desktop copy to avoid markdown escaping the hidden-worktree path. Three standalone SELECTs then produced the environment, properties and dependency results recorded below.

## Production environment evidence returned by user

The standalone SELECT result is: database Transfer; ProductVersion 15.0.2000.5; snapshot state OFF; RCSI false; database VIEW DEFINITION=1; database ALTER=1; SELECT on sys.sql_expression_dependencies=1. This confirms actual OFF under that login, not a transition. It does not establish table SELECT permissions or successful export/restore. The user will have a DBA evaluate enabling snapshot; no production setting has been changed by this work.

The next two SELECT-only diagnostics have now been returned by the user. Every affected object has SELECT=1, zero indexes and zero triggers. The four affected views carry MS_DiagramPane1 (nvarchar, 5,468 to 7,006 bytes) and MS_DiagramPaneCount (int). WRA_WATERLEVEL_STATION carries table/column MS_Description plus a custom nvarchar table property. All reported character properties use Chinese_Taiwan_Stroke_CI_AS. No property values were requested.

The qualified Transfer.dbo references resolve to database ID 5, equal to the current database ID, with matching existing user-table IDs. The two unqualified dependencies also resolve to existing user tables. This establishes same-source identity and existence; selected-object membership must still be checked during each actual export.

## Compatibility implementation plan

1. Preserve immutable class-1 table/view/column extended properties for the evidenced nvarchar, int and SQL NULL values. Include ownership, native type, declared length, collation and raw hex in the schema fingerprint. Preserve unsupported types as named preflight failures; never silently omit them. Keep ordinary comments available to the UI, with only one property output path.
2. Reconstruct nvarchar directly into sql_variant with its original COLLATE expression; preserve int and NULL semantics. Hex payloads prevent embedded quotes, newlines or GO text from changing script structure. Keep DataOnly free of property DDL. Index/trigger restrictions remain explicit.
3. Pin Microsoft.SqlServer.TransactSql.ScriptDom 180.117.0 (net472, MIT, no package dependencies) in the existing packages.config workflow. Use its SQL2019 parser for this bounded view-reference support. Resolve source database/object identity through the source SQL Server catalog, using parameters and the current owned connection/transaction.
4. Rewrite only catalog-confirmed same-source three-part named table/view references into local schema/object references. Use parser token offsets, retain comments/literals, reparse the output, and reject unsupported qualified contexts. Retain original source definitions and dependency identities in fingerprints; use resolved local IDs for ordering/cycle detection. Table expressions, external database/server references, unresolved/unselected objects and unsupported contexts remain blockers.
5. Run test-first regressions for properties and parser behavior. Add guarded Properties and References integration profiles to the existing opt-in runner, using the already authorized dedicated LocalDB only. Compare restored native property metadata/bytes, exercise schema/data modes and restricted database users, and prove views follow the differently named target while the source remains accessible with deliberately different rows.
6. Run affected smoke tests, Debug/Release builds, and independent code review. Record actual SQL engine/client identities and cleanup evidence. SQL2025 LocalDB evidence does not certify SQL2019 production; the user still needs DBA-reviewed SNAPSHOT enablement and a real production retry.

Package evidence: https://www.nuget.org/packages/Microsoft.SqlServer.TransactSql.ScriptDom/180.117.0 . Native property semantics: https://learn.microsoft.com/en-us/sql/t-sql/functions/sql-variant-property-transact-sql and https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-addextendedproperty-transact-sql .

## Implementation and local acceptance complete

- Added fixed prerequisite diagnostics, precise SNAPSHOT states and retry without discarding wizard choices. Actual restricted-user tests verify ON without ALTER, missing VIEW DEFINITION, denied table SELECT and denied dependency-catalog SELECT.
- Preserved supported native typed properties; independently verified scopes, declared metadata, collation and raw values after restore. Same-source qualified view table references now use parser/catalog checks and restore to local target objects; the source-online divergence test passed.
- Final 169 smoke groups plus AutoReleasePolicy, Debug/Release builds, Properties/References/Core actual LocalDB export/restore and independent scoped reviews passed. Full evidence, hashes, the DB_ID provider-type correction and cleanup records are in verification.md.
- Changes remain on codex/mssql-export-preflight-compat for local acceptance. Original master is unchanged. Production SQL2019 retry still requires DBA-reviewed SNAPSHOT enablement for data modes and may reveal unsupported view syntax not present in the returned metadata; no production success, merge, push or release is implied.
