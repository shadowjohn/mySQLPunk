# MSSQL SQL logical export

This exporter creates a local UTF-8 SQL script for explicitly selected SQL Server tables, optionally including supported dependent views. It is a logical export, not a native `.bak`, and does not change scheduled backups or other providers.

## Source and restore requirements

Data export uses one owned connection and one SNAPSHOT transaction. The source database must already have `ALLOW_SNAPSHOT_ISOLATION ON` with state `ON`; the exporter does not enable it or fall back to weaker isolation. Schema-only export does not require a data snapshot. The account needs SELECT plus readable definitions for the selected objects and dependencies. Missing prerequisites and unsupported features block preflight.

Restore to a prepared, compatible database using **one sqlcmd process/session for the entire artifact**:

```powershell
sqlcmd -S '<target-server>' -d '<target-database>' -E -b -x -f 65001 -i '<export.sql>'
```

`-b` stops on SQL errors; `-x` disables sqlcmd variable substitution; `-f 65001` specifies UTF-8 input. Do not split/reconnect between GO batches: large rows use session-local staging. The existing GUI SQL import splitter is not the validated import path. Review the destination before restoring: schema-and-data creates objects and does not drop/replace existing objects; data-only assumes compatible existing objects. Re-running a whole artifact is not an idempotent restore.

For the tested official **Go sqlcmd v1.10.0**, input is natively UTF-8 and `-f` is not supported. Use its explicit alternative command:

```powershell
& '<verified-portable-go-sqlcmd.exe>' -S '<target-server-or-named-pipe>' -d '<target-database>' -E -b -x -i '<export.sql>'
```

The installed classic sqlcmd 15.0.1300.359 fails when this fixture's input crosses 2 GiB. Do not use that version for >2 GiB artifacts. The Go client's UTF-8 decoder and one retained database connection are documented in its [version-pinned source](https://github.com/microsoft/go-sqlcmd/blob/v1.10.0/pkg/sqlcmd/sqlcmd.go#L339-L373). Local compatibility tests check Unicode codepoints/catalog names, literal substitution syntax, GO parsing, same-session temporary state and fail-fast behavior. On this LocalDB environment Go requires the exact running instance pipe (`np:\\.\pipe\LOCALDB#<instance-id>\tsql\query`); the shorthand `(localdb)\name` did not resolve. Never substitute another instance. See verification for the large-file result and client provenance.
## Supported behavior and boundaries

Native scalar types retain precision and temporal offsets, raw ANSI/Unicode/binary values retain bytes, and NULL differs from empty or trailing spaces. Supported schema includes identity seed/increment, computed columns, rowversion definitions, defaults, keys, supported indexes, checks, foreign keys, comments and supported dependent views. Computed and rowversion values are regenerated, not copied. Identity source allocation history/gaps are not promised. Empty tables retain their declared identity seed. Named filegroups must already exist on the destination.

Large values are streamed in bounded chunks; a row may span multiple GO batches. The importer validates ordered chunks, totals and completion before its one target INSERT. Replaying a consumed row's terminal batch does not insert it twice. A failed row is not replaced with NULL, even if a client continues after errors. **Earlier successfully inserted rows may remain:** restoration of the whole file is not all-or-nothing. Stop and investigate any sqlcmd error.

Legacy `text` rows use typed staging even for small values because direct varbinary assignment to text is rejected by SQL Server. This preserves the source codepage but increases script size and restore work for those rows. `ntext`, `image`, untyped XML and ordinary WKT text are supported within the documented tests; native SQL Server `sys.geometry` now uses `Serialize()` -> bounded binary chunks -> `geometry::Deserialize()`, with small values optionally inline. Native geometry DDL, NULL/empty, serialized bytes, SRID and Z/M are retained without WKT conversion, simplification or MakeValid. Geography, user-defined/alias CLR types, spatial indexes, typed XML, sql_variant, unsupported dependencies/table/column/index features and unreadable definitions are rejected rather than silently omitted. Tables with spatial indexes remain blocked in all modes, including data-only; geometry value support does not imply spatial-index scripting. Refer to the current preflight details for the affected object/column.

The transaction remains open while source rows are exported, retaining snapshot versions. It is released before local hash verification and mirror work. Allow disk space for source version store, SQL hex expansion (often more than twice binary payload), partial output, verified local output and optional mirror copies. Progress reports actual source bytes/rows and separate file verification/mirror work; estimates can remain unknown. More bytes in an export do not mean rows have been successfully restored.

Cancellation waits for owned reader/command/transaction/file cleanup. The final consent stamp detects destination changes observed before publication. Windows File.Replace does not provide an atomic compare-and-swap against unrelated external writers: a change in the narrow final-check/replace window can still be displaced. Recovery paths are retained and reported; after a partial replacement error, the previous file may be at a recovery path instead of its original location. Use a new filename for externally managed targets. Before replacement starts, failure or cancellation preserves the existing output. If the local file was published but the mirror fails or is canceled, the result is **local completed, mirror incomplete**, not complete success. Any retained recovery paths are reported.

## Reproducing the isolated acceptance

The opt-in runner defaults to instructions only:

```powershell
.\tests\Run-MssqlExportIntegration.ps1
.\tests\Run-MssqlExportIntegration.ps1 -Run -Server '(localdb)\mySQLPunkExportTest_20261012' -Profile Core -OutputDirectory '<new-empty-directory-under-authorized-FixtureRoot>'
# Profiles Geometry, Large and Concurrency use the same explicit arguments.
# Official portable Go client is opt-in: -Client Go -SqlcmdPath <verified-exe>.
# Profile ClientProbe verifies client semantics before a large restore.
# Large -RestoreArtifact <prior-owned-export.sql> validates prior SHA/token, then restores it in a new fixture.
```

This task's runner deliberately accepts only its authorized dedicated instance and the local authorization manifest at `.superpowers/sdd/implementation-plan/fixture-environment.json`. It requires that instance to be running, installed sqlcmd, Integrated Security and sufficient free C: space. It does not install/start databases, read saved connections/credentials, or accept a user database name. Each invocation generates GUID `_src`/`_dst` databases with an ownership token, bounded MDF/LDF sizes and exact file paths, then validates token/names/paths before DROP. Outputs and evidence remain local under the fixture root; no raw large artifact is committed. Never work around an antivirus block.

Core uses actual service artifacts, catalog comparison and streaming SHA-256 row digests. Large uses independent 1×/10× processes, actual file length, single-session restore and 25 ms memory sampling by service phase. Concurrency uses actual source connections with controlled second-connection updates, DDL, blocking/cancellation, disconnect and filesystem faults. The verifier retains only per-row digests, never whole field values. SQL fixture setup batching is separate from actual artifact restoration.

See [verification evidence](design/mssql-export/verification.md) for exact PASS/FAIL/PENDING scope. Native wizard/device sound/long-job UI, SSH lifecycle and GUI import require their own evidence; local engine acceptance does not establish those, production, IIS or user-account acceptance.

## Geometry restore compatibility

The Geometry profile verifies native geometry on SQL Server 2025 (17.0.4025.3), including a 17,600,048-byte serialized value, with real service output restored through one classic sqlcmd session. It compares streamed Serialize() hashes plus catalog, SRID, Z/M and empty/validity observations; invalid but storable shapes remain invalid and unchanged. Malformed serialized bytes or invalid chunk ordering fail the guarded row without inserting a partial value.

This is SQL Server's internal spatial serialization format, not portable OGC WKB. Cross-version engine compatibility and theoretical maximum-size geometries require their own restore acceptance. The client remains bounded; source Serialize(), target Deserialize() and staging still require SQL Server memory/tempdb/log/disk resources. See [Geometry design](design/mssql-export/geometry-design.md) and [verification](design/mssql-export/verification.md) for exact tested boundaries.
