# Database Report Export Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development; independent file ownership permits parallel workers. Track completion here.

**Goal:** Deliver the approved native WinForms three-step schema report wizard with six templates and real cancellable file export.
**Architecture:** Collect metadata on a dedicated connection owned and disposed by the worker. Render an in-memory schema snapshot to DOCX/XLSX using OOXML and PDF using PDFsharp-GDI, write to a sibling temporary file, validate, then atomically publish. The modal wizard owns cancellation and UI progress; a PNG sprite atlas displays Punky with proper alpha in both themes.
**Tech Stack:** Existing .NET Framework 4.7.2 WinForms, ZipArchive/XML, PDFsharp-GDI 6.2.4 (net462), existing ThemeManager and database providers.
**Spec:** docs/design/report-export/design.md; user approved 2026-10-06, including classic DOCX header vertical middle and 5px left padding.

## Global Constraints
- Preserve current files' BOM/encoding/newlines and unrelated changes; no commit/push.
- Report schema only: no data rows, no connection strings or credentials in documents/logs.
- All table names selected by default, exact name matching; search does not change hidden selection.
- Three formats and two stable template IDs each; native sample open plus thumbnail enlarge.
- Classic DOCX header cells use vertical center and 75 twips (5px at 96dpi) left padding.
- Never run background queries on the active UI connection; clone settings and own SSH lease through ConnectionOpenService.Open(settings, false).
- Cooperative cancellation waits for an in-flight provider call; no UI-thread disposal. Never mark cancelled/failed output successful.
- Output only publishes after validation; preserve existing file on cancellation/failure, explicit overwrite consent.
- Use current theme colors; transparent animation with pause, dispose timers/images.

## Review Focus
- Exact case-distinct table selection and unusual XML/Excel names: round-trip names and text safely.
- Cancelling during metadata/writer work: original destination survives and temporary files removed.
- Long CJK comments or wide schemas: wrapping/repeated headers/page continuation, no silent clipping.
- Provider metadata errors: fail clearly, do not silently produce a complete-looking partial document.
- Closing form while busy and repeat exports: worker owns lifetime; no callback after disposal or leaked connection.

## Shared interfaces
All types in mySQLPunk.lib. SchemaReportDocument: string DatabaseName, EngineName, Title, Author; DateTime GeneratedAt; List<SchemaReportTable> Tables.
SchemaReportTable: string Name, Comment, Ddl; List<SchemaReportColumn> Columns; DataTable Indexes.
SchemaReportColumn: string Name, Type, Nullable, Key, DefaultValue, Comment, Extra.
ReportExportFormat: Docx, Xlsx, Pdf. ReportTemplate: string Id, Name, Description, SampleStem; ReportExportFormat Format.
ReportTemplateCatalog.ForFormat(ReportExportFormat) returns IEnumerable<ReportTemplate>; Find(string) returns ReportTemplate or throws.
Writers: public static void Write(string path, SchemaReportDocument report, ReportTemplate template, CancellationToken token, Action<int,int> progress) on SchemaReportOpenXmlWriter and SchemaReportPdfWriter. Parent validates/publishes; writers create provided temporary path. Call progress at completed tables, check cancellation within loops, do not open databases.
ReportExportRequest: string DatabaseName, EngineName, Title, Author, TemplateId, OutputPath; List<string> Tables; bool Overwrite.
ReportExportProgress: string Stage, Message; int Completed, Total; bool IsIndeterminate.
SchemaReportExportService.ListTables(Func<IDatabase>, string databaseName, CancellationToken) returns List<string> and owns factory connection.
SchemaReportExportService.Run(Func<IDatabase>, ReportExportRequest, CancellationToken, IProgress<ReportExportProgress>) returns void and owns collection connection and transactional file output.
ReportExportWizardForm(Dictionary<string,object> connection, string databaseName) clones settings; no active IDatabase passed.
ReportText.Get(string chinese,string english) resolves existing Localization.CurrentLanguage for localized report UI/data labels.

## Task 1: Contract, collection and publication (parent)
Files: lib/SchemaReportModels.cs, lib/SchemaReportExportService.cs, tests/ReportExportTests.cs, tests/Run-ReportExportTests.ps1.
- [ ] First assert missing export types with reflection against current app; record expected failure.
- [ ] Implement shared contracts and six-template catalog; normalize supported provider metadata while retaining comments, extra, null/default distinctions.
- [ ] Use isolated open factory; checks before/after each metadata query. MySQL table comments use schema metadata; never SQLite GetTableStatus because it counts data rows.
- [ ] Test real temporary SQLite schema, selected subset, Chinese names, cancellation, failed overwrite and cleanup.

## Task 2: Office writers and classic sample
Files: lib/SchemaReportOpenXmlWriter.cs, tests/ReportExportTests.OpenXml.cs; sample generator/docx-classic and matching thumbnail.
- [ ] Test produced zip/XML parts, exact text, classic header margins/alignment, formula-looking strings remain text, safe unique worksheet names, cancellation.
- [ ] Implement two DOCX templates and two XLSX templates against shared models; XML escaping/control character handling; pagination/header styles.
- [ ] Update classic sample and verify all header cells have center and 75 twips, body unchanged.

## Task 3: PDF writer
Files: lib/SchemaReportPdfWriter.cs, tests/ReportExportTests.Pdf.cs; parent owns package/csproj edits.
- [ ] Test real PDF documents for both templates, CJK content, multi-page long comments, cancellation.
- [ ] Implement readable PDF with embedded Windows CJK font, repeated column headers, wrapping and page numbers; fail clearly if usable font unavailable.
- [ ] Render and inspect output pages with local PDF tooling.

## Task 4: Native wizard, assets and integration
Files: ReportExportWizardForm.cs, PunkyReportLoadingControl.cs, Form1.cs, mySQLPunk.csproj, packages.config, report-templates/ assets; docs and history.
- [ ] Native three-step resizable themed wizard, table filtering/all-none, template cards, thumbnail/sample open, SaveFileDialog, progress/cancel/error/success.
- [ ] Closing during work requests cancellation and awaits completion; disable navigation/restart while busy. Initial load cancellable; retain selections and output location.
- [ ] Build alpha sprite atlas from approved animation; timer only while running, pause and reduced-animation fallback; use static poster after terminal state.
- [ ] Wire relational database right-click next to existing data dictionary, retain existing HTML exporter.
- [ ] Build Debug and Release, run dedicated export tests + full existing smoke suite; native rendered form checks using test-owned fake/SQLite DB only.
- [ ] Fresh review, resolve material findings, update history and user docs with verified boundaries.

## Execution ledger
Ruling: Continue on codex/report-export-wizard in the existing checkout so approved uncommitted samples/assets remain available. File ownership separates parallel work. No commit/push.
Ruling: User's explicit '可以進行下一步開發' authorizes execution; no duplicate plan approval gate.
Ruling: Preserve metadata cancellation as cooperative because IDatabase exposes no command cancellation. UI explains waiting for the current query.
Preflight: Tasks 1/2/3 share only the models/signatures above; parent owns models and csproj/package edits. Task 4 consumes the service signature; samples writer owns only sample directory. Tests are split by format. All tasks agree with .NET472 and metadata-only boundaries.

Progress: Tasks 1-4 source implemented; initial Debug/native temporary-SQLite run passed 417 report assertions and 106 existing smoke tests. Review fixes landed. Latest Debug/Release builds pass; final runtime verification and production sample refresh remain pending after antivirus quarantined ReportExportTests.exe at11:19. Default report runner is inactive, batch artifact overwrite removed, test executable excluded from release. See docs/design/report-export/implementation-status.md. No final-completion claim.
