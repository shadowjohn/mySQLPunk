# Scoped MySQL repair review

Reviewed 2026-10-16 against the supplied mysql-repair-diff.patch and current MySQL/shared files in the repair worktree. Scope: plan Task 1 and common ownership/publication/form-lifetime constraints. Oracle/SQLite provider changes were excluded. This was a static review only: no build, test rerun, database, saved connection, SSH session, or user dump was opened. The implementer/root test results remain their evidence, not a new result from this reviewer.

## Decision

Changes requested for the three focused issues below. The original comment/literal redirection, fresh-table SkipExisting, status=NO, generated-column, bounded-page, owned-session and atomic-publication repairs are materially present. No additional blocker was found in the common raw-provider/SSH ownership or publication helper during this pass.

## Findings

### P1 - SkipExisting still writes existing data for accepted INSERT forms

Location: mySQLPunk/lib/MySqlImport/MySqlImportRunner.cs:33-35 and 394-407.

The policy recognizes only INSERT/REPLACE [IGNORE] INTO. With an initial table old, SkipExisting, and `INSERT LOW_PRIORITY INTO old VALUES (1);` or `INSERT old VALUES (1);`, this regex does not match. Neither the lexer nor target validation rejects these forms, so execution reaches ExecSQL at line 411 and writes the existing table, while the job can report Completed. This contradicts the visible "Skip existing objects and their data" policy. The separate initial catalog repairs the original fresh-table omission but does not close this bypass. MySQL's documented INSERT grammar permits priority modifiers and optional INTO: [official INSERT syntax](https://dev.mysql.com/doc/refman/8.4/en/insert.html).

Fix locally by extracting the write target from tokens for accepted INSERT/REPLACE forms, or reject unrecognized write forms under the protected strategy before execution. Add durable data-only cases with initial old plus a fresh table, modifiers, omitted INTO, and quoted/qualified identifiers; assert no protected-table write reaches the provider.

### P2 - Native trigger NEW/OLD qualifiers are rejected as database names

Location: mySQLPunk/lib/MySqlImport/MySqlImportRunner.cs:638-670, especially the alias construction at 643-658 and throw at 669-670. Export emits trigger DDL at mySQLPunk/lib/MySqlExport/MySqlSqlExportService.cs:298-316.

`CREATE TRIGGER tr BEFORE INSERT ON t FOR EACH ROW SET NEW.id = 1;` passes shared preflight but EnforceTargetQualifiers does not recognize trigger pseudo-rows. NEW is neither a registered table alias nor an object prefix, so execution fails with "Ambiguous database qualifier 'NEW'". OLD behaves the same. The exporter includes selected trigger DDL and defaults IncludeTriggers to true; its existing test fixture uses an empty BEGIN END trigger, so it misses this ordinary native round-trip case. [MySQL trigger pseudo-row semantics](https://dev.mysql.com/doc/refman/8.4/en/trigger-syntax.html).

Recognize NEW/OLD only within a recognized trigger definition without weakening ordinary database qualifier enforcement. Cover a native-style DELIMITER trigger with NEW/OLD references, one real database-qualified object, and a literal containing the same text; preserve pseudo-row/literal tokens and redirect only the database object.

### P2 - Single-table export still selects unrelated database objects

Location: mySQLPunk/Form1.MySqlExport.cs:29-30; mySQLPunk/MySqlExportWizardForm.cs:634-640. Defaults: mySQLPunk/lib/MySqlExport/MySqlExportModels.cs:53-55.

The new single-table route restricts SelectedTables, but its options retain IncludeViews/IncludeRoutines/IncludeTriggers=true. The form then supplies every loaded view, routine and trigger to the service. A schema-and-data export of one table therefore also emits DROP/CREATE for unrelated database objects, including triggers whose tables were not exported. This leaves the accepted single-table route repair incomplete and can modify unrelated objects on restore. DataOnly suppresses these blocks, so the current routing test (DataOnly only) does not expose it.

For the explicit single-table entry point, default related-object flags off or constrain them to verified dependencies/owning-table triggers. Retain full-database defaults on the database entry point. Extend actual route/form coverage to SchemaAndData with unrelated objects and confirm the constructed export options contain only the authorized scope.

## Reviewed safety boundaries

- WizardOwnedSession keeps the wrapper as owner and exposes NativeDatabase separately; ConnectionOpenService passes the raw provider while SecuredDatabase.Dispose releases provider then SSH lease in finally. MySQL pooling is disabled for the owned job. The shared copy omits cached runtime provider/connection-string fields and sets an explicit target without modifying the source dictionary.
- Both native MySQL forms keep busy=true until awaited work returns and defer close while busy. Cancellation sources are disposed after await. No provider work survives ShowDialog return through those routes.
- SqlExportPublication uses same-directory Replace/Move, and export retains the completed staging artifact when publication was attempted and failed. Earlier failure/cancellation preserves the previous destination. The source snapshot setup and successful rollback are checked before publication; generated fields are omitted from INSERT and source pages are bounded explicitly.
- Real MySQL/MariaDB value fidelity, generated/trigger server round trips, source preservation with two actual databases, CASE provider type inference, SSH disposal and command cancellation remain unverified. No claim of full provider parity or production acceptance is made.

## Scoped correction re-review

Reviewed the supplied mysql-corrections-diff.patch and current source once, limited to the three findings above and direct fix breakage. No build, test rerun, database, saved configuration or SSH operation was performed. Oracle/SQLite changes were excluded.

- Finding 1, P1 SkipExisting INSERT forms: **addressed**. MySqlImportRunner.cs:641-651 extracts INSERT/REPLACE targets from tokens with priority modifiers, IGNORE and optional INTO. The protected policy uses that result at 390-405, and target redirection uses the same extraction at 662-667. Unidentifiable write targets fail under SkipExisting instead of falling through. The added MySqlImport_Regression_ModifiedInsertProtectionAndTriggerPseudoRows at tests/SmokeTests.MySqlImport.cs:454-514 checks eight forms against mixed initial/fresh tables, a data-only initial-table fixture, qualified targets without USE and literal preservation.
- Finding 2, P2 trigger NEW/OLD: **addressed** for the reported native trigger path. MySqlImportRunner.cs:659-661 permits pseudo-row qualifiers only after identifying a CREATE TRIGGER definition; real object positions still take precedence during redirection at 696-699. Trigger ON table positions are recognized at 674-676. The added regression covers a DELIMITER trigger with NEW/OLD, a real qualified trigger table/body INSERT and source-looking literals, plus rejection of the pseudo-row exemption outside trigger DDL (tests/SmokeTests.MySqlImport.cs:496-511).
- Finding 3, P2 single-table related-object scope: **addressed**. Form1.MySqlExport.cs:30-35 disables views/routines/triggers only for the explicit selected-table route. The form consumes those flags at MySqlExportWizardForm.cs:114-116, while database-wide defaults remain intact. WizardRepairs_MySqlTableRouting now checks both actual table handlers in SchemaAndData and DataOnly and asserts all three unrelated-object flags are false (tests/SmokeTests.WizardRepairs.cs:12-23).

**Scoped verdict: approved.** No material direct regression was found in these three corrections during this static pass. The implementer's appended report records a fresh isolated compilation and 12 passing MySQL provider methods; that remains implementer evidence. Root's final application builds/full smoke suite and all previously stated real-server, SSH and production boundaries remain pending or unverified by this reviewer.
