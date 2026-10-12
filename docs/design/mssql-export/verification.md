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
