# MSSQL export verification — 2026-10-12

This is service/engine evidence from an isolated C: worktree, separate from native wizard, routing, audio and production acceptance. Only the explicitly authorized `(localdb)\mySQLPunkExportTest_20261012` instance was used, with Integrated Security and generated ownership-token-checked `_src`/`_dst` databases. The instance had idled to Stopped; it was explicitly restarted under the existing authorization outside the runner. The runner itself never starts or installs a server.

## Environment and commands

- SQL Server LocalDB `17.0.4025.3` (SQL Server 2025).
- System.Data SqlClient, .NET Framework assembly `4.0.0.0`; smoke process x64.
- sqlcmd x64, file version `2019.0150.1300.359`, installed ODBC 170 client. PE architecture and help/version saved by the runner.
- Source/target database collation `Latin1_General_100_BIN2`; tempdb `SQL_Latin1_General_CP1_CI_AS`; ANSI columns additionally use `Chinese_Taiwan_Stroke_CI_AS`.
- Local-only evidence root: `C:\Users\johnho\AppData\Local\Temp\mysqlpunk-export-20261012-7cadb673332543c3b17c61924490a702`.

```powershell
.\tests\Run-MssqlExportIntegration.ps1 -Run -Server '(localdb)\mySQLPunkExportTest_20261012' -Profile Core -OutputDirectory '<fixture-root>\task10-core-final-eeffe3a4581f41ffa17bf4a545c0e8af'
.\tests\Run-MssqlExportIntegration.ps1 -Run -Server '(localdb)\mySQLPunkExportTest_20261012' -Profile Concurrency -OutputDirectory '<fixture-root>\task10-concurrency-1221f034404f49ae8990486c49cb296e'
.\tests\Run-MssqlExportIntegration.ps1 -Run -Server '(localdb)\mySQLPunkExportTest_20261012' -Profile Large -OutputDirectory '<fixture-root>\task10-large-5fbeb319575947a298118473ae3ea3b9'
```

The runner invokes the existing SmokeTests executable with one opt-in filter, never ReportExportTests or a new executable. Large launches two independent sequential processes (1× then 10×), and each stage uses the final service, not manually concatenated writers. Default invocation prints usage without building/connecting. `evidence.json` records exact generated names, sqlcmd arguments/exit, source row hashes, artifact SHA-256, file length, service elapsed time, phase memory peaks and token-checked cleanup.

Classic client artifact restore uses exactly one command:

```powershell
sqlcmd -S '(localdb)\mySQLPunkExportTest_20261012' -d 'mysqlpunk_export_<generated-token>_dst' -E -b -x -f 65001 -i '<owned-artifact.sql>'
```

## Core results

| Status | Acceptance |
| --- | --- |
| PASS | Actual service-generated 178,948,508-byte Core artifact restored with sqlcmd exit 0. Mirror completed; real worker connections were closed before file verification/publication/mirror. |
| PASS | Catalog equality: native column type/length/precision/scale/collation/nullability, identity seed/increment, computed definitions/persistence, defaults, PK/UQ/index ordering/DESC/INCLUDE/filter/disabled state, FK/check state, view body/SET options and comments. Only outer whitespace around module definitions is normalized. |
| PASS | Streaming SHA-256 raw-value row multiset comparison, with explicit NULL and length markers; decimal38, temporal ticks/offsets, Unicode/unpaired surrogates/NUL, raw ANSI, empty and trailing spaces. Computed values compared; rowversion bytes excluded from equality and checked as regenerated 8-byte values. |
| PASS | Two schemas with the same Unicode/right-bracket table name; positive/negative identity subsequent inserts and fresh empty identity seed. Source allocation gaps are outside the contract. |
| PASS | 67,108,864-byte binary, 16,793,600-byte Unicode, multi-LOB/no-PK, large text/ntext/image, independently small ntext/image, untyped XML whitespace, >2 MiB ordinary WKT text and literal GO/:r/$(name) text. All raw value hashes match. |
| PASS | Existing destination CREATE collision returns nonzero sqlcmd exit, without an implicit replacement path. |
| PASS | Live preflight blocks native geometry/geography, sql_variant, alias type, typed XML, unselected sequence dependency, encrypted definition, SNAPSHOT OFF, and SELECT-only reader denied VIEW DEFINITION. |
| PASS | Actual service DataOnly artifact copies with missing, duplicated or swapped chunks, conversion error and repeated terminal batch fail under sqlcmd -b. A deliberately continuing same-session client also cannot insert the malformed row or substitute NULL; replay leaves the earlier row plus exactly one guarded row. |

The first Core attempt exposed an actual product defect: small legacy `text` was emitted as direct varbinary assignment (SQL Msg 206). Commit `14ba2fc` routes these rows through existing typed raw-byte staging. A focused tiny-text test failed before the change and all four values tests passed afterward. The final Core run verifies preserved source codepage bytes; ordinary small `ntext` and `image` also pass independently. Staging small legacy text has additional script/restore cost. Other intermediate Core failures were harness expectation corrections (outer view whitespace; denied-definition Preflight throws rather than returns issues), not product data changes.

## Concurrency and failure results

| Status | Acceptance |
| --- | --- |
| PASS | A second source connection updates parent and child in one transaction between the export's table reads. Restored parent and child both contain the old generation; source contains the new generation. |
| PASS | Schema changed after snapshot start fails the export and preserves the prior file. Closing the owned source connection during the job fails and releases it. |
| PASS | Live read cancellation, controlled write/verify/mirror faults, actual verify/mirror-read cancellation, and a Windows concurrent target content replacement preserve the appropriate old/racing file or confirmed local result. Owned partials are cleaned. Controlled stream failure represents disk-full/IO behavior; the disk was not physically filled. |
| PASS | An uncommitted ALTER TABLE holds a real schema lock while the worker is waiting for metadata; cancellation completes in 633 ms with source disposal, old output preserved and blocker rollback. |
| PASS | Source connections are closed before local verification/mirror; mirror failure/cancel reports local completed, mirror incomplete. |
| PENDING | Real UNC/share removal or physical disk-full event; only controlled stream/filesystem fault coverage is claimed. |

The controlled overwrite-race test changes the file before the final consent-stamp check. It does not prove atomic protection against unrelated external writers in the final check/File.Replace window. File.Replace has no compare-and-swap guarantee; displaced prior content can remain in a reported recovery path after a partial replacement error. A new filename is recommended for externally managed targets.

## Capacity and memory

**PASS:** 1x service, single-session restore, hash/catalog and cleanup. **PASS:** 10x actual >2 GiB artifact, export/verify/mirror and measured bounded process memory. **FAIL with installed classic client:** 10x restore crosses its 2 GiB input limit. **PASS with official Go v1.10.0:** the unchanged saved 10x artifact restored completely in one session; 10,240 rows, streaming hashes, catalog and exact-owned cleanup all passed.

Classic sqlcmd15.0.1300.359 returned Msg102 at row7,961, after exactly7,960 committed rows. The affected132,302-byte statement crosses input byte2,147,483,648 (line95,541, offset2,147,365,047). After normalizing only the row sequence and consumed watermark, it is byte-identical to a chunk already accepted for row1; normalized SHA256 `02292c14e069f1e52d323046d1f11211c4cf59ea26145bd999bd00bfac9be15e`. This supports a client input-boundary defect, not a new writer change. Microsoft staff have also documented [classic sqlcmd truncation at2GB](https://learn.microsoft.com/en-us/answers/questions/248682/sqlcmd-breaks-input-insert-string). The failed run and diagnosis remain in Large-10; both owned databases were cleaned. The alternative official Go client subsequently restored the same saved artifact successfully without another exporter run.

| Metric | 1x | 10x |
| --- | ---: | ---: |
| Rows | 1,024 | 10,240 |
| Source binary payload bytes | 134,217,728 | 1,342,177,280 |
| Actual SQL file bytes | 276,231,755 | 2,762,444,660 |
| Service elapsed including verify/publication/mirror | 8.789 s | 105.212 s |
| Data peak working set / private bytes | 62,398,464 / 47,181,824 | 63,320,064 / 47,919,104 |
| Verify peak working set / private bytes | 60,321,792 / 44,920,832 | 62,115,840 / 46,460,928 |
| Mirror peak working set / private bytes | 60,489,728 / 44,945,408 | 62,152,704 / 46,465,024 |

Data peaks grew approximately 1.5%, verify/mirror about 3%, while source rows/payload grew exactly 10x. No proportional total-data memory growth was observed. File phase peak changes were under 2 MiB and consistent with process/runtime allocation variation. The elapsed difference includes local IO/cache/environment variability; this is not a controlled throughput benchmark.

Artifact SHA-256 (final Core and original Large service output):

- Core: `685148d4d9cab08c3eca6244b4b484e17efcb39525c02869eae26e0634869881`
- 1x: `49c54297650993a739c837b17531475f9c14d57c2d1388bf550229f5f2c2469f`
- 10x: `7679cf0f779eb9044a845d0f8dfdc32de14de89f73973ce52ca93b6d3adeb24b`

 The verifier hashes fields through a 64 KiB buffer; per-row digests are retained only for comparison, outside the service-memory measurement. Independent service processes are sampled every 25 ms, including periods when the worker is blocked. Phase peaks are working set/private bytes, not SQL Server or sqlcmd memory. These measurements establish this fixture's behavior, not a universal upper bound on all metadata or workloads.

## Explicit pending boundaries

- SSH endpoint unavailable: job tunnel success/cancel/failure cleanup and main-connection continuity remain PENDING.
- Native wizard long-job responsiveness, device sound exactly once/mute and human UI acceptance remain PENDING in this engine worktree; HTML or fake-source tests are not substituted.
- GUI import path, browser/IIS/production/user-account acceptance remain PENDING/not exercised.
- Older SQL engines with missing catalog columns and feature-specific CLR/FILESTREAM/AlwaysEncrypted/partition environments were not installed or enabled. Current-engine preflight/fake metadata coverage does not establish those environments.
- Single fields >1 GiB or near theoretical type limits remain optional PENDING stress, outside the approved v1 required gate.
- No packaging, release, merge, push or antivirus changes were performed. Large artifacts are local-only and are not in Git.

## Official Go client compatibility

Controller approved a portable official client only within the C: fixture root; no installer/system client replacement. [Microsoft v1.10.0 release](https://github.com/microsoft/go-sqlcmd/releases/tag/v1.10.0), `sqlcmd-windows-amd64.zip`, official release asset SHA-256 `a4c28332fcc6e497d655e53ac8f4939f4ab170f9cfd32d0fa5081b60f4d9d691` matched. Extracted EXE SHA-256 `d9dbd1a8bd26213747b246deba1e13663a7e1def530cd961d7582b41a5e27852`; Authenticode Valid. Provenance/help/log files remain in `sqlcmd-client-9ced1042941b441e88f61425c091e60f`.

This client's `-f65001` fails as an unsupported flag. The agreed contract is UTF-8 input, substitution disabled, fail-fast and one connection; `-f` is classic-client syntax. The pinned [IncludeFile source](https://github.com/microsoft/go-sqlcmd/blob/v1.10.0/pkg/sqlcmd/sqlcmd.go#L339-L373) explicitly wraps input in a UTF-8 decoder with BOM override and reads input incrementally. The same module retains one `sql.Conn` across batches, bypasses variable replacement with `-x`, and exits on qualifying SQL errors with `-b`.

`task10-go-probe-a846216a8dd943f0b0cca4d750b5c24d/ClientProbe-1`: PASS native UTF-8 Unicode codepoints/catalog identifier, literal `$(name)`, GO inside string versus batch delimiter, temporary table surviving GO, and SQL error preventing the following batch. Successful probe exit 0; intentional fail-fast probe exit 1. Both owned databases cleaned.

Go did not resolve the shorthand `(localdb)\mySQLPunkExportTest_20261012`: the failed probe is retained under `task10-go-probe-a944d2473e664c7b8be2b4aa3766305b`. Resolving only the already authorized instance's running pipe via `sqllocaldb info` passed. The explicit equivalent command is:

```powershell
& '<verified-go-sqlcmd.exe>' -S 'np:\\.\pipe\LOCALDB#<authorized-running-id>\tsql\query' -d 'mysqlpunk_export_<generated-token>_dst' -E -b -x -i '<owned-artifact.sql>'
```

No input splitting or independent sessions are used. The opt-in runner exposes `-Client Go -SqlcmdPath <verified-exe>`; `-Profile ClientProbe` checks compatibility. `-Profile Large -RestoreArtifact <prior-owned-export.sql>` checks original token/evidence/SHA, copies unchanged bytes to a new owned fixture, regenerates deterministic expected rows and compares streaming hashes/catalog after restoration. It preserves the prior autogenerated PK name solely for catalog equality.

Additional Go module-text parser probe: `go-parser-e8b7c0d85a264e8f84b7e65d9cc614bc`, **9/9 PASS**, using a separately generated token-owned small database. Matrix: multiline block comment, multiline N-string and single-line comment, each containing GO, `:r` to a confirmed nonexistent owned path, or literal `$(name)`. GO/:r/variable tokens begin at column 1 inside multiline contexts; line-comment tokens follow `--`. Every case exited 0 with its marker; string outputs retained literal tokens. The include path was absent before/after and never opened as a directive. Exact name/token/datafile checks passed before database DROP. Tiny SQL, logs, results and cleanup evidence remain local; `.superpowers/sdd/implementation-plan/task-10-go-parser-probe.ps1` reproduces the bounded diagnostic. The long restore session was not interrupted and no checkout build ran concurrently. This proves these SELECT-only valid quoted/comment contexts, not malformed modules or bare directives outside SQL syntax.

## Standalone checks and review

PASS `tests/Run-SmokeTests.ps1 -Configuration Release`: 133/133 plus AutoReleasePolicy, with prior filters/live opt-ins saved, cleared and restored. PASS `build.bat Debug`, `build.bat Release`, and `git diff --check`. Logs: `.superpowers/sdd/implementation-plan/task-10-full-smoke.log`, `task-10-build-debug.log`, `task-10-build-release.log`. No AV block occurred. Packaging was not run; existing smoke includes release exclusion/policy checks. The later Go harness extension compiled and its live client probe passed. Controller accepted checkpoint `c67e9b4752d5ab9eccd284a7cab76885061124d1` after read-only review; final combined branch tests remain controller-owned.

## Final >2 GiB restore result

**PASS** `task10-go-large-e61519c425e545e4b47c4ecfc00e3bca/Large-10`: the unchanged 2,762,444,660-byte final-service artifact (SHA-256 `7679cf0f779eb9044a845d0f8dfdc32de14de89f73973ce52ca93b6d3adeb24b`) restored with official Go v1.10.0, one process/connection, `-E -b -x -i`, and the authorized instance's `np:\\.\pipe\LOCALDB#D570B7E8\tsql\query` endpoint. Exit 0; restore elapsed **22:00.8247112**, separate from the original 105.212-second service export/verify/mirror measurement.

All **10,240** rows match the independently regenerated source through streaming field hashes including lengths/null markers. Sorted-row digest SHA-256: `57B5ACA8A4E28FBDF32752E1483102512C93A0721C790BB2EADBED9383669407`. All catalog comparisons passed. Source/target `mysqlpunk_export_d415addfbd444f3ca3079283c636bff2_src/_dst` were checked against exact names, ownership token and file paths before DROP. Successful restore log, evidence JSON and catalog comparisons remain local. Existing SmokeTests integration filter and AutoReleasePolicy exited successfully; no new executable or alternate restore service was used.

Reproduction command (the output directory must be fresh):

```powershell
.\tests\Run-MssqlExportIntegration.ps1 -Run -Server '(localdb)\mySQLPunkExportTest_20261012' -Profile Large -Client Go -SqlcmdPath '<fixture-root>\sqlcmd-client-9ced1042941b441e88f61425c091e60f\extracted\sqlcmd.exe' -RestoreArtifact '<fixture-root>\task10-large-5fbeb319575947a298118473ae3ea3b9\Large-10\export.sql' -OutputDirectory '<fixture-root>\<new-empty-directory>'
```

Both client outcomes are retained: the tested classic version is unsuitable above this boundary; the tested official Go version meets the complete-file restore gate. This does not establish performance or compatibility for every sqlcmd/SQL Server version. Remaining SSH/native UI/older-engine/optional stress boundaries above are unchanged.

## Combined branch and native fixture evidence

Controller integration commit `bb38aa3` combines reviewed engine, native wizard, routing and acceptance harness. The only merge conflicts were additive smoke registrations; Wizard, Routing and Integration are all retained exactly once.

- PASS Windows PowerShell `tests/Run-SmokeTests.ps1 -Configuration Release`, exit 0: runner reports 146 groups, including the live integration opt-in group intentionally SKIP without explicit `-Run`. This does not repeat or replace the separate Core/Large/Concurrency evidence above. All existing MYSQLPUNK process-environment filters/opt-ins were saved, cleared and restored.
- PASS AutoReleasePolicy, explicit `build.bat Debug`, explicit `build.bat Release`, and `git diff --check`. Existing `nuget.exe not found, packages directory detected` fallback warning remains; successful builds are not warning-free. No packaging/release or AV change occurred. Logs: `.superpowers/sdd/implementation-plan/final-combined-smoke.log`, `final-combined-build-debug.log`, `final-combined-build-release.log`.
- Native Computer Use observation, after explicit user retry: light/dark 1102x762 four-step fixture, schema/comments, large animated Punky on the right, one mute control, dual measured fixture bars, stale ETA becoming unknown, and delayed close awaiting cancellation cleanup. Terminal canceled state and Close/Reset accessible names were observed. Preview windows were closed through UI before builds.
- A real `ShowDialog` fake-service regression caught and verified the WinForms CancelButton modal-exit fix: cancel export keeps the result visible after cleanup; titlebar close waits and then exits. Root-column layout was exercised at 640x480 with 1/1.5/2 simulated control scaling. These are control tests, not physical monitor DPI observations.
- Still PENDING: actual audible playback, physical 100/150/200% DPI, native wizard driving a long real database job, SSH endpoint lifecycle, GUI import and production acceptance. No user saved connection or production database was accessed.

Final whole-branch review and the single scoped fix re-review are complete; their verdict and amended-code checks are recorded below. The original master checkout and its pre-existing untracked files remain untouched; no master merge, push, tag or release has occurred.
## Final review and amended-code verification

The broad review of `7507adc..bb38aa3` found 0 Critical, 4 Important and 2 Minor issues. One coordinated fix wave, `0c0009a777f43d96df2d77e5aeeb361674ff068e`, addressed all six. Independent scoped re-review reports all six ADDRESSED and no new Critical/Important breakage. Reports are retained locally as `final-review.md`, `final-fix-report.md` and `final-rereview.md` in the plan's ignored SDD workspace; public outcomes are below.

| Resolved issue | Final evidence |
| --- | --- |
| Native sizes were always unknown | Bounded heap/clustered/LOB allocation metadata, optional-query fallback to null, no table COUNT or new DMV permission. Real tables returned 16,384 and 172,032 bytes and fed the weighted estimator. Mutable sizes/row estimates remain excluded from definition fingerprints. |
| Preflight lost object/column identity | Control regression displays the exact schema/object/column, safe issue code and reason, with a database-level fallback. |
| Missing old-engine index option became OFF | Missing capability remains null and the writer omits SQL2019-only syntax. Static capability-to-DDL regression passes; actual SQL2017 restore remains PENDING. |
| DataOnly lacked session settings | Common SET preamble, no object DDL. A real service artifact restores into an indexed computed-column target using the exact documented classic command without an added -I. |
| Scope/counters absent from UI | Control tests cover views, computed/rowversion regeneration, snapshot resource note, completed/total tables, row counters and estimated percent. Phase ETA remains explicitly phase-only. |
| Durations wrapped at 24 hours | Pure formatter regression preserves 25:02:03 for elapsed and ETA. |

PASS amended-code focused groups: metadata5, schema6, worker4, wizard10; final Release metadata5. AutoReleasePolicy passed. Root separately ran explicit `build.bat Debug` and `build.bat Release` at final code head; both passed (`final-fixed-build-debug.log`, `final-fixed-build-release.log`). Existing NuGet cached-package fallback warning remains. The full146-group run above was before this narrow fix; it is not represented as a new full-suite run. No >2GiB exporter/restore repeat was needed for these metadata/UI/session-preamble changes; the earlier exact large-artifact evidence is retained with its original provenance.

Final tiny live evidence: `<fixture-root>/final-fix-verified-813444899eb0494fa7429419ea15435d/FinalFix-1/evidence.json`, engine17.0.4025.3. The actual DataOnly artifact is395bytes, SHA256 `30438aa2f50d52957dde52e90e5471960b62c61c788ed83c4cb958a8c7f190c9`. One classic sqlcmd process with `-E -b -x -f 65001 -i` (no -I) exited0 and restored both rows into the indexed computed-column table. Exact token `11ad52b49eee49d0b0603b820a013845` source/target DBs were checked and dropped. Reproduce with the opt-in runner's `-Profile FinalFix` and a new empty directory under the same authorized root; all standard ownership gates apply.

A final-head native preview was launched with the existing fixture-only SmokeTests executable, then the user accidentally pressed physical Escape before the first window observation. The user explicitly resumed, but the tool immediately continued reporting its stopped state. No further window inputs, screenshots, process termination or executable rehosting occurred. At that point, actual final-head visual observation of the amended labels was PENDING, not passed. The earlier native fixture observations and the new control tests retained their separate scopes. The preview was not closed through a bypass. The subsequent user-requested retry below resolves this specific visual-observation gap.

All of the review's listed unjudged environment/scope items were retained: SSH; physical DPI/audio and native real long-job; GUI import/production/IIS/browser/account; real UNC/disk-full/power-loss; optional >1GiB single-field/theoretical limits; older/feature-specific engine environments; future spatial codec and spatial indexes; File.Replace external races/recovery paths; limited native replacement-error layouts; inherited NuGet warning; identity allocation-history exclusion; compatible-target/non-idempotent/partial-restore and resource requirements; prototype-only evidence; other providers/native/scheduled backups and publication. Previously deferred continuation and 2MiB metadata-budget coverage is resolved. No unjudged environment is silently promoted to PASS.

All required implementation work and mandatory isolated tests are complete. The explicitly pending acceptance boundaries above remain open. Feature branch `codex/mssql-export-wizard` is retained; no master merge, push, tag, packaging or release was performed. The ignored SDD authorization manifest/evidence is retained under ruling13 so the guarded runner stays reproducible.

## Final amended-label native retry (2026-10-12)

PASS user-requested Computer Use retry against the existing final-code fixture executable at `mySQLPunk/bin/CodexVerify/mySQLPunk.SmokeTests.exe`, product code `0c0009a`, documentation head `1410b25`. No rebuild or product edits were needed. Both windows were 1102x762; test title was `FIXTURE ONLY`.

- Dark, schema and data: schema/comment/estimated-row/estimated-size columns and selection totals visible. Confirmation fully showed view scope, computed/rowversion regeneration, same SNAPSHOT transaction and version-store cost. No label overlap was observed.
- Running in both themes: estimated 43%, 12,345,678 bytes, tables 0/2, rows 50/100 and the separate 50% table bar were visible. Initial phase ETA 00:00:03 and speed 2,500 bytes/s changed to waiting/estimating and unknown speed after the fixture stopped reporting progress. These are synthetic values, not a new throughput measurement.
- Punky visibly advanced through multiple poses automatically in both themes. Only a mute control was present; the dark-theme click changed its caption to enable sound. This confirms UI behavior, not audible playback or audio-once timing.
- Dark cancel: disabled cancel and cleanup wording were observed before terminal canceled stage/footer and both canceled queue rows. The result could be scrolled fully into view, including 0/2 completed, zero result rows, view/computed/SNAPSHOT notes, no published file, and the complete restore-client notice. Close and Reset remained visible. Cancel correctly kept the result window open.
- Light, data only: the views checkbox became unchecked/disabled and confirmation stated all views excluded. During running, titlebar Close first showed cancellation/cleanup and then the window exited. Dark terminal Close also exited normally. Final window inventory contained no fixture windows, and both launched process IDs had exited; no process kill or executable rehosting was used.

Scope: real native rendering/interaction with a fake service, not HTML screenshots. The fixture does not open a DB or write/publish SQL; its preflight can inspect the generated destination, which was kept unchanged. No database acceptance, build, full suite, release or production action was repeated. Actual audio, physical DPI, native long real-DB jobs, SSH and the other explicitly pending boundaries above remain unverified. The earlier Escape interruption remains recorded, but final amended-label visual observation is now PASS.

## Native Geometry extension: isolated restore evidence (2026-10-12)

This explicitly authorized extension supersedes only the earlier native geometry rejection. Geography, user-defined/alias CLR types and spatial indexes remain rejected. The existing wizard, source transaction, artifact ownership and publication boundaries are retained.

PASS `Geometry` profile using the actual service and installed classic sqlcmd, SQL2025 `17.0.4025.3`, 64-bit .NET Framework. Evidence: `<fixture-root>/geometry-9285b5a115c54830bfb449fef2697d95/Geometry-1/evidence.json`; ownership token `039c89c494de483190be66a93f0ea582`. Both exact token-owned source/target databases were checked and dropped.

- Stream-generated WKB was uploaded using a stream parameter for fixture construction. The native large value serialized to **17,600,048 bytes**, exceeding 16 MiB. Production export uses server Serialize() and sequential bounded GetBytes; no client spatial package or whole-value materialization was introduced.
- Complete service artifact: **36,193,053 bytes**, SHA-256 `6e30d3b286eef4fddd34abc39c1d08f39c56e98a7b7d6609097f1b085babcf49`; 10 rows across 3 tables. Service export/verify/publish elapsed 1.562496 s; one classic `-E -b -x -f 65001 -i` restore exited 0 in 4.8491934 s. These are this fixture's measurements, not a throughput promise.
- Source/target raw Serialize() streaming hashes and catalog equality PASS. Cases include NULL, empty, points, polygon, collection, curves, invalid but storable geometry, multiple geometry fields with ordinary scalar/binary fields, SRID 0/4326/3826, Z/M and the large value. Independent spatial observations confirmed the tested semantics; no MakeValid, reprojection or simplification occurred. GeometryLarge row digest: `ED402280096B33C712A88EC55AFF3987A781A13AEB858ED2FCF7A1C343134083`.
- Actual SchemaOnly and DataOnly artifacts restored successfully. Missing, duplicate and reordered chunks, malformed serialized content, and replayed final batches were tested with fail-fast sqlcmd and a continuing importer; the failing row stayed absent, while replay retained one already committed row. All-mode geography and spatial-index preflight rejection PASS, with no published file.
- Export Data-phase sampled process peaks: working set 64,618,496 bytes, private bytes 51,924,992 bytes (42 samples). Fixture generation/upload was sampled separately. This single run is not a scaling benchmark or universal memory ceiling; SQL Server serialization/deserialization and staging resources are separate.

The first live run had already passed artifact restoration, byte hashes and catalog, but its observation query failed with SQL24144 because STGeometryType() rejects invalid geometries. The verifier was corrected to use STIsValid/SRID and raw serialized bytes for that row, while valid values retain type/Z/M checks. The corrected full Geometry profile above passed; earlier failure evidence remains in the ignored extension workspace. This was a verifier correction, not geometry repair.

Cross-version internal serialization compatibility, physical native UI/audio/DPI, long real-job UI, SSH, production, geography, spatial-index DDL and near-theoretical size limits remain unverified or unsupported as stated. Earlier >2GiB acceptance remains separate and was not rerun for this codec extension. Final product commit `92fda819f72b9307df5c37d7901d966b845dd1cf`: focused metadata6/values5/LOB4/schema6, full existing smoke151 (live integration explicitly SKIP in that run), AutoReleasePolicy, explicit Debug/Release builds and diff check PASS. Existing cached-package/NuGet-executable warning remains; no new dependency. Logs/report are in `.superpowers/sdd/geometry-implementation-plan/`. Independent review of immutable extension diff `5edf69d..e3d4998` is APPROVED with no actionable findings. The reviewer independently traced native metadata identity, same-SNAPSHOT projection, bounded continuation, final Deserialize rollback/poisoning and the actual malformed-payload restore log, without rerunning tests or DB work. Report: `.superpowers/sdd/geometry-implementation-plan/geometry-review.md`. Its unjudged cross-version, theoretical-capacity, server-resource, geometry-scaling and previous UI/SSH/production boundaries remain as stated; no additional acceptance is implied.

The affected Core profile also passed after the verifier began resolving native types through user_type_id: `<fixture-root>/geometry-core-9fc8f25c34d04967855d22531dfb9f65/Core-1`, artifact178,948,509bytes SHA256 `80afc52efd6cb8a00b4f312428f2bf91d2274f7e7b684e5bcb417005f4ce9159`, ownership token `2bcec8beffef4c2b95f693a047f47cd4`. Existing64MiB binary/>16MiB Unicode, catalog/raw hashes, identity/computed/rowversion, mirror, unsupported/visibility/SNAPSHOT gates, and all five chunk-fault/continuing-importer cases passed. Exact owned DBs were cleaned. This rerun replaces no earlier Large/Concurrency/native UI evidence.

## Production extended-property and permission acceptance (2026-10-13)

The current production-compatibility branch adds explicit prerequisite diagnostics/retry and typed extended-property support. Independent focused reviews found no material defects in either scope. Before the view-reference implementation, the full existing smoke suite passed162 groups plus AutoReleasePolicy; this was local test/control evidence, with ordinary live integration explicitly skipped.

PASS actual `Properties` profile on the already authorized dedicated SQL2025 LocalDB17.0.4025.3, using installed x64 classic sqlcmd15.0.1300.359 with one `-E -b -x -f 65001 -i` session per artifact. Evidence: `<fixture-root>/production-properties-5e26735bfbf84520aa7a82bcf9188a4e/Properties-1/evidence.json`; owner token `ee7741b98efe4f6d841029a75ac1dd8f`. Exact owned source/target databases were checked and dropped. The instance had stopped while idle; it was restarted under the existing dedicated-instance authorization, without changing production.

- SchemaAndData artifact36,011bytes, SHA256 `2c54e7b60914e17cdba6dd3167d771bdf27337da4db2cbf09d8d66050a9fe078`, restored successfully into a differently named target. Native property ownership/type/scope/column name/name/BaseType/MaxLength/Precision/Scale/Collation/raw hex/NULL compared equal.
- Cases include SSMS diagram text7,006bytes, near-limit7,400bytes, int min/max, custom table/view/column properties, descriptions, empty literal/declared empty, short value with long declared type, Unicode/surrogate pairs/quotes/CRLF/GO/sqlcmd-like text and trailing spaces. Source/target defaults are BIN2 while property text uses Chinese_Taiwan_Stroke_CI_AS. A dropped source column creates an ordinal gap; restored properties attach to the correct column by name despite new ordinals.
- SchemaOnly and DataOnly artifacts succeeded; schema-only restored empty tables with exact properties; data-only preserved preexisting target properties and inserted data without duplicate property DDL. Actual value/collation drift changed fingerprints and failed publication while preserving an existing output file. Unsupported varchar/decimal properties and indexed/triggered views remained explicit preflight failures.
- An owned database user WITHOUT LOGIN, with ALTER=0, successfully exported data with SNAPSHOT already ON. Missing VIEW DEFINITION produced the typed diagnostic and no file; denied table SELECT blocked data but permitted schema-only export; denied SELECT on sys.sql_expression_dependencies produced the safe permission category. No server login or production grant/configuration was changed.

The production user supplied SQL2019 catalog metadata, not an export/restore result. Actual production retry, SQL2019 engine restore, final native retry-button observation, physical DPI/audio and long real production-job behavior remain pending. Same-database reference implementation and its separate acceptance are still in progress at this checkpoint.

## Same-source view reference acceptance (2026-10-13)

PASS actual `References` profile on SQL2025 LocalDB17.0.4025.3 with the same classic x64 sqlcmd15.0.1300.359 client. Evidence: `<fixture-root>/production-references-5f2a6b157d9f446f9aaba83f2d383f53/References-1/evidence.json`; owner `ce219c380e0e47fc83028d72d38b100a`. Exact owned source/target databases were checked and dropped. Artifact2,132bytes, SHA256 `d4db43b6f632e7ddec9a6a3cef3a3b94d3e53c23512db557c2b4bb2bb62630f2`, restored in one process with exit0.

- Bracketed/double-quoted qualified references, inter-token comments, database-like literals/comments, CTE/nested joins and a dependent ALTER VIEW restored successfully. Dependency ordering created the base view before its dependent view.
- Source remained online and accessible. After exporting `exported`, source was changed to `source_only`. Both target views still returned `exported`; after a target-only update both returned `target_only`, while the source view remained `source_only`. Target catalog dependencies had local resolved IDs with no server/database qualifiers.
- Actual other-database, omitted-schema, unselected-object, four-part-column and existing-local-server four-part references all failed preflight and preserved existing output files. No linked server was created. Pure parser tests separately cover malformed modules, unsupported function/rowset contexts, quoted identifiers, exact token preservation and incomplete catalog coverage.
- The first actual run failed before publication: SQL Server's DB_ID result was smallint/System.Int16, incompatible with SqlDataReader.GetInt32. A direct provider metadata probe established the cause. Resolver SQL now explicitly converts both DB_ID expressions to int; the full References profile above passed after this correction. The generic dependency reader already used conversion and needed no change. Failed-run evidence is retained separately; both failed-run owned databases were cleaned.

Independent scoped review found no material reference/parser/identity/fingerprint/writer issues. Unit RED/GREEN evidence covers helper and metadata integration; final reference7, metadata6, schema6 and properties5 focused groups passed before the live run. Production SQL2019 retry and restore remain unverified; DBA-reviewed SNAPSHOT enablement is still required for data export on the reported OFF database. No production settings/data/permissions, original checkout, merge/push/release or account configuration were changed.

## Final combined compatibility verification (2026-10-13)

After the actual provider DB_ID type correction, the References profile above passed and the complete Properties and Core profiles were rerun on the final implementation. All used the existing authorized SQL2025 LocalDB17.0.4025.3 and classic x64 sqlcmd15.0.1300.359; all exact token-owned source/target databases were checked and dropped.

| Profile | Final evidence directory under fixture-root | Main artifact | SHA256 |
| --- | --- | --- | --- |
| Properties | production-final-properties-9a069435643e42e5a3ee5710c6baec83/Properties-1 | 36,011 bytes | 310060f624dc7a88d9c0c481df77ae670a97001a995fda403e4c6f9e6d5624f6 |
| Core | production-final-core-1f38975b8fae448e86965d83241a2bd8/Core-1 | 178,948,915 bytes | ea20fbdf2b752a1e3559ee3493449633f0e7ad1ce387efb15197a369005d31c4 |

Properties again passed the complete native typed-property comparison, all three modes, value/collation fingerprint drift, unsupported-feature rejection and actual restricted-user cases. Owner token: 4585a1b484bc4809b1e4803debc43f8b. Core passed one-session main-artifact restore (exit0), raw streaming hashes/catalog equality, identity/computed/rowversion, mirror, permission/SNAPSHOT/unsupported-object gates and all five adversarial continuation/replay cases. Owner token: ef4fadf700b842ac9e67bd518a24d0ca. Expected negative-case restore exits remain recorded in evidence.json and are not main-artifact failures.

Fresh full Release smoke: **169 groups passed**, plus AutoReleasePolicy. Live integration is explicitly skipped by that ordinary suite and is evidenced separately above. Explicit sequential `build.bat debug` and `build.bat release` both passed. ScriptDom18.0.0.0 was verified in both output directories. Original working-file BOM/newline styles were preserved for all18 modified tracked files; direct Git blob comparison was unsuitable because this checkout uses core.autocrlf=true. `git diff --check` passed. Logs: `.superpowers/production-preflight/final-full-smoke.log`, `final-build-debug.log`, `final-build-release.log`, `final-properties-live.log`, `final-core-live.log` and `references-live.log`. Cached-package restore remains the local build path; no new clean-machine installation claim is made.

Three independent scoped reviews (diagnostics/retry, properties, parser/dependencies) reported no material findings. Final production SQL2019 export/restore, actual production view bodies outside the bounded FROM/JOIN support, native retry-button observation, physical DPI/audio and long real production jobs remain pending. The reported Transfer database still has SNAPSHOT OFF and needs DBA-reviewed enablement for data modes; no production ALTER/GRANT/connection was performed. Prior Geometry, >2GiB and native UI evidence retains its original provenance and was not rerun by this compatibility fix. No merge/push/release has occurred.


## Optional SNAPSHOT final isolated acceptance (2026-10-13)

The approved optional-mode policy supersedes the historical snapshot-required/no-ALTER statements above. Default ordinary READ COMMITTED, strict trusted-FK restore, optional ALTER-gated SNAPSHOT and independent restoration warnings were exercised on SQL2025 LocalDB17.0.4025.3, x64 System.Data4.0.0.0, classic x64 sqlcmd15.0.1300.359 (ODBC170). All seven profiles ran sequentially with explicit Release configuration, using the original authorization manifest and ownership/path/reparse/20GiB gates. No product fix was necessary in Task6; intermediate failures below corrected test orchestration assumptions.

**PASS:** seven profiles,57 named scenario checks,29 actual one-session restore invocations:17 exit0 and12 expected negative exits. Negative exits cover the deliberate ordinary mixed-time FK547 and adversarial artifacts, not failed successful-export acceptance. Each evidence.json retains export mode/cleanup/state/journal/audit, every restored artifact's bytes/SHA256, actual sqlcmd command/exit and source/target comparison assertions. Client identity is recorded in each parent directory's sqlcmd-identity.json.

| Profile | Scenario checks | Restore exit0 / expected nonzero | Evidence directory under fixture-root |
| --- | ---: | ---: | --- |
| OptionalSnapshot | 8 | 5 / 1 | `s6-Opt-5e1bb1dd953f42a0a95e6535fdf43206/OptionalSnapshot-1` |
| SnapshotLifecycle | 16 | 3 / 0 | `s6-Sna-35a6c4368d5349a0b81c2380ee8679aa/SnapshotLifecycle-1` |
| Core | 8 | 1 / 6 | `s6-Cor-1aed161256254b93b143dd4f8fc853e3/Core-1` |
| Concurrency | 11 | 1 / 0 | `s6-Con-af9ae29a15a543f5a8ba21e0752bc2ef/Concurrency-1` |
| Properties | 4 | 3 / 0 | `s6-Pro-9cc561c189ff46178211189cffb8d16d/Properties-1` |
| References | 3 | 1 / 0 | `s6-Ref-e7f71045b06049fcb4297edf71889d26/References-1` |
| Geometry | 7 | 3 / 5 | `s6-Geo-325d078a080e4eff9769ff46e7f5f506/Geometry-1` |

Representative actual artifacts (all restore artifacts, including deliberately corrupted copies, are hashed separately in evidence.json):

| Profile / artifact | Bytes | SHA256 |
| --- | ---: | --- |
| OptionalSnapshot / mixed-time-True.sql | 2,153 | `7a7ac2742352fe5247f7bb213fe8b4f216762689d35ffc709baae3b287307193` |
| SnapshotLifecycle / blocked-off.sql | 2,153 | `fbf66d1ad65997c143c7853f868f199c5eda73a74ae3d76de83ce38590e2f7a2` |
| Core / export.sql | 178,948,946 | `a8030a7570b2d2bb2c7ce9a9b3a35f0862155ad2ed6ef08a1c9bd48fe2102852` |
| Concurrency / export.sql | 2,125,694 | `d233f3f120973824a93882606bff6dd9aab79cdd47f2f60af72da221a425ae3c` |
| Properties / properties-all.sql | 36,042 | `a6a7f10372e586ea8a233383ff8037c0315ced238b9aaf0fca038c04ec566841` |
| References / references.sql | 2,163 | `12dc5849bf4130f6e0500db55302b3c4ea97e7236ce51d325d7a45f4d1e461e8` |
| Geometry / geometry.sql | 36,193,084 | `23d2843a0b4cbe216610b9fea549b75c408742a152d53aefae37dc8f48d11472` |

- Ordinary initial OFF is genuine fresh-database OFF. Owner and WITHOUT LOGIN/no-ALTER users exported and restored under both OFF and ON; source state stayed unchanged and backend settings audit remained empty. Required table SELECT denial still failed. Explicit Parent-before-Child consumption allowed a legal second-connection insert Parent2/update Child→2: ordinary artifact restored Parent1/Child2 and failed exactly FK_Snapshot_Child_Parent/547; selected SNAPSHOT restored Parent1/Child1 with enabled, trusted FK. Ordinary cancellation and real DDL drift preserved the old output.
- OFF→ON→OFF and original ON preservation passed completion/cancel/read-error cases. A real owned write transaction produced ON transition3 before release/cancel/timeout. Final true enable timeout was60.1196531s, actual state0/RestoredOff. A separate owned SNAPSHOT transaction actually read a row, produced OFF transition2 and held it through the independent30s cleanup:30.7162529s, artifact Completed and restored exit0, cleanupUnconfirmed, actual finalON, durable journal2bdc14c3827d461cad0c7476e725117b.json. Releasing that reader still leftON after ALTER cancellation; fixture-owner explicit OFF was teardown, not product restoration.
- Same-DB second acquisition returned busy in the backend audit. Already-owned Target acquired independently while the first source lock remained; both reads ended before restoration, and both settings returnedOFF. An earlier diagnostic attempt to restore Target while the older source SNAPSHOT remained observed DISABLE_VERSIONING, Unconfirmed and finalON. This engine version-retention wait is distinct from app-lock independence. [Microsoft's DISABLE_VERSIONING definition](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-os-wait-stats-transact-sql?view=sql-server-ver17) explains its earliest-active-transaction timestamp wait; the LocalDB observation is retained in task6-wait-observation.md.
- Live ALTER revocation failed safely before ON. A fixture-local journal obstruction sent no ALTER. Real ON followed by an injected acknowledgement exception restoredOFF. Exporter-only SqlConnection.Close retainedON/Unconfirmed/journal; these last two are fault injection, not proof of real network-loss handling.
- Actual hidden same-executable child PID31748 was terminated only after parent-verified durable journal andON readiness (job199c27cb-5f06-4245-a297-09d45a694ed2). State remainedON and the next read-only notice did not repair it. Child revalidated server/manifest/token/root/reparse ancestry/physical DB paths; owner OFF and database DROP were separately recorded fixture teardown.
- Final14 generated source/target databases were dropped. Across every Task6 successful/failed attempt, all28 generated databases have individual drop timestamps and retained evidence; a fresh exact-name whitelist query returned0 remaining. All artifacts/journals remain local-only. No production database was touched.

Failed evidence is retained under fixture-root: OptionalSnapshot-640700b3d22c4529863e72c224d6947e (wrapper must complete with original frozen-schema identity); SnapshotLifecycle-f192aa29e33940e3a32df104a77b8ddb (canceled OFF revertsON); SnapshotLifecycle-65ef985e996b49ed8da5d675e5689b6d and SnapshotLifecycle-9269500aba7244f9a8a4541062d3cafc (cross-DB version retention); snapshot-final-OptionalSnapshot-c96457625d2948f98647653f49fb67a9 (verbose evidence prefix exceeded legacy .NET path length before publication). Only harness choreography/evidence-path naming changed; ownership, restore and product assertions were preserved. The initial RED runner rejected the new profile before any DB creation.

Final unfiltered `tests/Run-SmokeTests.ps1 -Configuration Release`: **194 groups PASS**, plus AutoReleasePolicy. The native IdentityUnknown notice assertion passed inside the recovery/status group. Explicit sequential `build.bat debug` and `build.bat release` both exited0; `git diff --check` exited0. Checks completed2026-10-13T08:12:18.3975724Z. Logs/exit manifest: `.superpowers/sdd/snapshot-options-implementation-plan/task6-full-release-smoke.log`, `task6-build-debug.log`, `task6-build-release.log`, `task6-final-checks.json`; live summary and28-name cleanup inventory/readback are beside them. Task6 and whole-branch independent review remain controller-owned and pending.

Boundaries: SQL2019 production export/restore, SSH, actual network/power failure, physical DPI/audio and human long production jobs remain unverified. Native WizardService fixtures cover the real WinForms IdentityUnknown/mismatch notices and mode/cleanup surfaces without reading user journals; they are not human production acceptance. Earlier >2GiB evidence retains its original date and was not rerun; current Core/Geometry restore and streaming hashes exercise the unchanged codecs with the new lifecycle. Cached packages built successfully where noted; nuget.exe remains absent, so no fresh restore/install claim is made. No merge/push/release occurred.
