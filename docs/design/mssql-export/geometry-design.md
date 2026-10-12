# MSSQL Geometry binary export extension

Date: 2026-10-12. User authorization: continue `Geometry Serialize -> chunked binary -> Deserialize` after final native wizard verification. This extends the approved export design; the earlier v1 spatial rejection remains historical evidence, not the current intended geometry contract.

## Scope and data flow

Support SQL Server's built-in `sys.geometry` columns in the existing SchemaAndData, DataOnly and SchemaOnly workflows. Keep native `geometry` DDL. Project each non-computed source geometry as `CONVERT(varbinary(max),[column].Serialize())`, consume through the existing sequential bounded binary reader, and reconstruct with `geometry::Deserialize` in the target SQL. Small values may remain inline; larger values use the existing guarded varbinary(max) staging and chunk protocol. SQL NULL remains distinct from an empty geometry.

Recognize only the native type (`type_schema=sys`, `type_name=geometry`, not user-defined); its assembly-type flag is expected. Do not grant a name-only exemption to aliases or user CLR types. Preserve existing computed/rowversion handling and all other preflight checks. Geography and spatial indexes remain unsupported and explicitly blocked, including data-only tables with spatial indexes under the existing conservative index policy. No spatial metadata/DDL is silently discarded.

## Global constraints

- Reuse the existing .NET Framework 4.7.2 exporter; no new client SQL CLR/geometry package, no public API or UI flow redesign.
- Same source connection/SNAPSHOT and SequentialAccess; no whole-field GetValue/GetSqlBytes, byte-array materialization, WKT normalization, source reread, or extra SELECT per value.
- Preserve existing bounds: binary chunks <=64 KiB, small-row buffer <=256 KiB, value statements <=256 KiB, batches <=4 MiB, metadata-only setup/final INSERT <=2 MiB. No 16 MiB value rejection.
- Retain length/order/completion/failed/consumed guards, single final target INSERT, cancellation, cleanup, and verify-before-publication contracts.
- Geometry must preserve serialized bytes, NULL/empty, SRID and Z/M. Do not simplify, repair, reproject, or convert native values to WKT.
- Geography, aliases/user CLR types and spatial indexes remain preflight failures; identify the object/column or index in the existing safe diagnostics.
- Use only the already authorized `(localdb)\mySQLPunkExportTest_20261012` and generated token-owned C: fixtures. The existing manifest, ownership checks and guarded cleanup remain mandatory. Never touch saved connections, user databases, quarantined ReportExportTests.exe, antivirus or account state.
- Preserve each edited file's encoding/BOM and line endings, unrelated files, and original master. No merge, push, release or large artifact in Git.

## Acceptance

Focused tests must prove native-type discrimination, SQL projection/escaping, inline NULL/small values, large-row retained/partial/unread handoffs, bounded reads, accurate progress bytes and cancellation. Real service export and one-session classic sqlcmd restore must preserve streamed Serialize() hashes and catalog definitions for Point, NULL, empty, SRID 0/4326/3826, Z/M, collection, polygon and supported curves. Include a mixed scalar/multi-geometry/ordinary binary row and a native geometry serialized value >16 MiB. Compare independent SRID/Z/M/type observations as well as bytes. Malformed serialized content or omitted/replayed/out-of-order chunks must not insert the failing row or allow a later protocol batch to revive it.

The client verifier must identify CLR native types via sys.types joined on user_type_id, not TYPE_NAME(system_type_id). Hash geometry through Serialize() without materializing it. Server-side Serialize/Deserialize and restore staging necessarily consume SQL Server resources; bounded exporter memory does not imply bounded server memory. Actual SQL2025 is the available engine; cross-version internal-format compatibility, theoretical size limits, geography and spatial-index restoration remain outside this extension's acceptance.

## References

The user's [MSSQL dump article](https://3wa.tw/mypaper/?uid=shadow&mode=view&id=2760) supplies the serialization direction. Microsoft documents [Serialize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.types.sqlgeometry.serialize?view=sql-dacfx-161) and [Deserialize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.sqlserver.types.sqlgeometry.deserialize?view=sql-dacfx-161) as the paired SQL Server internal spatial binary format operations. Retain bounded streaming rather than adopting the article's whole-result SQL string accumulation.
