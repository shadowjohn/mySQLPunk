# MSSQL Geometry Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. The user has explicitly authorized this implementation direction; proceed without another approval gate.

**Goal:** Restore native geometry exactly using the current bounded SQL export pipeline.

**Architecture:** Add a native geometry codec to the existing binary projections, inline expressions and guarded large-row final projection. Preserve metadata/DDL and all current lifecycle/file guarantees. Extend the existing opt-in integration runner with a Geometry profile.

**Tech Stack:** .NET Framework 4.7.2, System.Data.SqlClient, PowerShell, existing smoke runner, authorized SQL2025 LocalDB and classic sqlcmd.

**Spec:** [geometry-design.md](geometry-design.md), extending [design.md](design.md).

## Global Constraints

- Reuse the existing .NET Framework 4.7.2 exporter; no new client SQL CLR/geometry package, no public API or UI flow redesign.
- Same source connection/SNAPSHOT and SequentialAccess; no whole-field GetValue/GetSqlBytes, byte-array materialization, WKT normalization, source reread, or extra SELECT per value.
- Preserve existing bounds: binary chunks <=64 KiB, small-row buffer <=256 KiB, value statements <=256 KiB, batches <=4 MiB, metadata-only setup/final INSERT <=2 MiB. No 16 MiB value rejection.
- Retain length/order/completion/failed/consumed guards, single final target INSERT, cancellation, cleanup, and verify-before-publication contracts.
- Geometry must preserve serialized bytes, NULL/empty, SRID and Z/M. Do not simplify, repair, reproject, or convert native values to WKT.
- Geography, aliases/user CLR types and spatial indexes remain preflight failures; identify the object/column or index in the existing safe diagnostics.
- Use only the already authorized `(localdb)\mySQLPunkExportTest_20261012` and generated token-owned C: fixtures. The existing manifest, ownership checks and guarded cleanup remain mandatory. Never touch saved connections, user databases, quarantined ReportExportTests.exe, antivirus or account state.
- Preserve each edited file's encoding/BOM and line endings, unrelated files, and original master. No merge, push, release or large artifact in Git.

## Review Focus

1. Built-in geometry has is_assembly_type=1; do not reject it or accidentally allow a user-defined same-name type.
2. Complete/partial/unread continuation prefixes and suffixes must not corrupt binary or double-count bytes.
3. Geometry NULL and empty values must survive both inline and staging without a deserialization error or NULL substitution.
4. Spatial indexes must remain blocked; CLR type IDs in the independent verifier must resolve by user_type_id.
5. A Deserialize failure must preserve protocol poisoning/rollback even if an importing client continues after the error.

### Task 1: Native geometry codec and real restore acceptance

**Files:**
- Modify `mySQLPunk/lib/MssqlExport/MssqlExportMetadataReader.cs`, `MssqlValueCodec.cs`, `MssqlLobScriptWriter.cs`; `MssqlDataScriptWriter.cs` or schema writer only if a verified seam requires it.
- Extend existing `tests/SmokeTests.MssqlExport.cs`, `.Values.cs`, `.Lob.cs`, `.Schema.cs` and their registrations in `tests/SmokeTests.cs` as needed.
- Modify `tests/SmokeTests.MssqlExport.Integration.cs`, `tests/Run-MssqlExportIntegration.ps1`; add `tests/MssqlExportFixtures/geometry.sql` if useful. Keep the original unsupported profile rejecting geography/UDT/spatial indexes.
- Controller owns public docs and history; implementer must not edit them. No csproj/dependency changes expected; discuss any required file outside this list first.

**Interfaces:** Consume existing SourceExpression/IsBinaryProjected/BinaryPrefix/BinarySuffix and IMssqlLargeRowWriter continuation contracts. Produce no new public API. The Geometry opt-in profile must run using the unchanged explicit fixture server/root/ownership guards.

- [ ] **1. Baseline and RED.** Run focused existing metadata/value/LOB smoke groups before editing. Add meaningful native-vs-user-type, source-projection/inline codec, NULL/empty/retained/partial/unread continuation and cancel/bounds regressions using existing strict readers. Run them and record expected failure on current code.
- [ ] **2. Implement.** Allow only native sys.geometry; retain geography/user types/index rejection. Reuse binary sequential reads. Inline and final staged expressions call geometry::Deserialize, handling NULL explicitly if SQL requires it. Keep other formats unchanged.
- [ ] **3. GREEN.** Run affected metadata/value/LOB/schema smoke groups and confirm bounded read/statement/batch behavior, accurate byte progress, no premature final row on cancellation, and no dependency changes.
- [ ] **4. Actual Geometry profile.** Generate small spatial fixtures and a >16 MiB serialized geometry without a whole large client string/byte array (a generated streaming WKB file/parameter is acceptable for fixture setup). Use actual service -> published SQL -> one classic sqlcmd session `-E -b -x -f 65001 -i`. Compare source/target geometry Serialize() raw streaming hashes, catalog types, and independently SRID/Z/M/type/empty; cover multiple geometry fields plus scalars/binary, NULL, points, polygon, collection and curves. Keep source file generation and DB resources recorded separately from exporter allocation.
- [ ] **5. Failure and compatibility.** Exercise malformed geometry bytes in a guarded staged row and existing chunk fault variants with an importer that continues, assert zero failing rows and no revived protocol state. Verify actual geography/spatial-index preflight rejection. Update the generic comparator to resolve native type via user_type_id and explicit geometry Serialize; run the affected Core profile once to ensure existing nonspatial/unsupported checks still pass.
- [ ] **6. Final verification and commit.** Run one complete existing smoke suite with live opt-in disabled, `build.bat Debug`, `build.bat Release`, and `git diff --check`, strictly sequentially with EXEs closed. Preserve/restore environment filters. Commit only owned product/test files. Report commands, RED/GREEN results, real fixture artifact/hash/size/cleanup, concerns and unverified boundaries in the assigned ignored report. No new UI preview or unrelated large-profile repeat needed.

### Controller completion

- [ ] Review immutable extension diff against this spec once after implementation. Resolve material findings with one coordinated fix and scoped re-review; avoid duplicate task/final reviews for the same single-task diff.
- [ ] Update current support docs/design matrix, history, and verification with actual evidence and residual boundaries. Existing prior v1 historical failures stay recorded. Preserve the original ignored fixture manifest under ruling13.
- [ ] Keep feature branch/worktree for user acceptance; no merge/push/release in this task.
