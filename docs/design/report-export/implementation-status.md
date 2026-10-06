# Native report export implementation status

2026-10-06: Report export is implemented; the user accepted the wizard flow and final Punky animation controls and authorized integration into `master`. Build/static checks and user-observed UI acceptance are distinct from the remaining report-runtime verification below.

## Implemented

- Database right-click report wizard, default-all exact table selection, three formats (three DOCX templates, two XLSX and two PDF templates), native sample/thumbnail opening, title/author/path confirmation.
- Dedicated worker connection including independent SSH lease, cooperative cancel, strict metadata errors, schema-only queries and atomic temporary-file publication.
- Native light/dark transparent Punky atlas, automatic export-time playback and a mute button.
- DOCX classic header center + 75 twips left margin, full and inventory XLSX, two paginated PDF layouts.

## Evidence and limits

Before the antivirus interruption, 417 report assertions and 106 existing smoke tests plus Auto release policy passed. The native test used its own temporary SQLite database, not a saved user connection. Latest source changes have not repeated that complete run.

At 11:19 the endpoint product reported `ReportExportTests.exe` as `Unauthorized file encryption` and quarantined it. Batch replacement of generated report fixtures is a possible trigger; this is not a confirmed false-positive ruling. Do not restore quarantine or add an exclusion merely to run these tests. The runner is now inactive by default. Its optional report artifact output requires an empty directory and never overwrites prior reports. No antivirus settings were changed.

The report test binary is not part of the application and is explicitly excluded from release packaging. Current bundled sample reports still come from the approved design generators. Updating their binaries/thumbnails to validated production-writer output is pending; `tools/report-previews/` contains scripts for writer QA (still pending) and separate read-only sample preview rendering (executed for the enlarged previews). The original two DOCX thumbnails are layout illustrations. The new schema-specification thumbnail uses a native Word rendering of its Python-authored specimen; it is not proof of production-writer runtime output.

Production/MySQL/SSH and native application export acceptance remain unverified. Word sample inspection is tracked separately from application-writer acceptance; Excel application acceptance is still pending. The interrupted generated files under `implementation-qa/` are local-only evidence and are not release inputs.

## Schema specification addition (2026-10-06)

User-approved supported scope is implemented as docx.schema-specification: portrait inventory, table identity/key/index summaries and seven-column editable field tables. It uses current metadata; FK diagrams and module grouping are omitted.

Fresh Debug and Release builds passed. Both copied the new specimen and native Word-rendered thumbnail identically. The Python-authored specimen passed read-only Open XML SDK validation with zero errors and all four Word-rendered pages were inspected. These results do not establish C# writer runtime acceptance. Added specification assertions and updated three-DOCX-card expectations remain source-only while the report runner is paused. Application export, long-table pagination and real-database validation remain pending.

## Wizard refinements (2026-10-06)

- Table picker displays exact table names with available comments and searches both fields. A single metadata query fetches comments on the dedicated worker connection; selection and export still use the original case-sensitive names, not display labels. PDF standard contents now append nonblank comments and retain existing wrapping/pagination.
- Confirmation page contains a responsive, right-aligned Punky up to 384 px. Begin export always starts animation automatically; completion/cancel stops it. No play/pause button is shown. Original MP4 audio is a 6-second local PCM WAV played once per export, never per loop. Mute/hide stops audio without replay on unmute. The mute control stays usable during export.
- Seven high-resolution sample excerpts open in a resizable fit-width viewer with scrolling, 100%, zoom and Ctrl+wheel. See preview-quality.md for rendering sources and sample-specific layout notes.
- Returning to settings or changing a completed request clears stale completion status and output links. Links use the completed request path, not an edited textbox.

Evidence: Debug and Release application builds passed. Both copied all seven previews and the three Punky assets byte-identically. All 73 atlas frames retain full alpha range; WAV is nonempty PCM16 stereo 44.1 kHz, 6.000 seconds. Table-selection and PDF-comment source review found no material regression. New metadata-picker assertions are source-only and were not executed.

The user supplied screenshots showing an earlier successful 274-table shadow export and PDF. This is user-observed evidence of the earlier version, not an independently reproduced run of these refinements. Updated native-window interactions, device audio, real-DB comment fetching and new PDF output remain unverified. ReportExportTests.exe was not compiled, restored or run; antivirus settings remain unchanged.

The enlarged preview pass also corrected the original classic/delivery specimen NULL column width (1.1 to 1.4 cm) and equivalent OpenXML property-validation issues. Word inspection confirms one/two pages respectively and all NULL headers on one line; both corrected specimens pass read-only SDK validation with zero errors. Their generators, docs/app copies and enlarged previews are synchronized. The application writers were not executed to create these samples.

## Mainline integration authorization (2026-10-06)

After the final autoplay/no-play-pause-button update, the user explicitly confirmed acceptance and requested merging the work into master. The earlier quarantine still limits automated runtime verification; integration does not represent a clean antivirus ruling or complete provider/SSH/export-format coverage. Report tests remain opt-in and excluded from application packaging.
