# MySQL / SQLite / Oracle wizard repair plan

Approved scope: the user requested repairs to the 2026-10-16 review findings. Baseline d41825cead810bc67f263753bb5f986a7f69c65c. Retain existing wizard architecture and defaults except unsafe behavior; no push/merge, real user databases, saved connection files or bulk restore.

## Acceptance and constraints

- Preserve each existing file's encoding/BOM/newlines. Keep unrelated PostgreSQL and MSSQL behavior unchanged.
- Use actual SQLite tiny private fixtures; MySQL/Oracle recording providers must follow real status=NO contracts. No live server acceptance claim without real evidence.
- Preserve old backup files on publication failure. Jobs use owned sessions, never cached editor sessions. No silent truncation, target redirection through string replacement, ignored commit failures, or fabricated spatial metadata.
- Review evidence: C:/Users/johnho/Documents/mySQLPunk-reviews/2026-10-16/review-summary.md and provider reports. Existing runtime reproductions are the baseline red evidence; add durable regression tests for repairs.
- Large unsupported values/features must stop explicitly before final publication, preserving originals; do not advertise universal type coverage.

## Task 1: MySQL correctness (independent provider files)
- [x] Regression: SkipExisting new table data, protected DROP, literal/comment-safe USE and qualifiers, generated columns and large values.
- [x] Token-based target enforcement, separate initially existing objects from current cache, preserve logical statement bytes/comments.
- [x] Apply requested initial table/mode selection in form; owned job session contract and bounded source behavior remain enforced.
- [x] Add tests in existing MySQL smoke files and report exact coverage/limits.

## Task 2: SQLite correctness (independent provider files)
- [x] Regression: every visible conflict policy, native DROP preserve/abort, trigger bodies, failed COMMIT and script transaction cleanup.
- [x] Generated columns, rowid/sequence, multiline/NUL/binary values, query-error handling and comments sidecar roundtrip.
- [x] Preserve text byte content and owned connection semantics; test tiny real SQLite roundtrips.
- [x] Add tests in existing SQLite smoke files and report coverage/limits.

## Task 3: Oracle correctness (independent provider files)
- [x] Check every execution result; implement transaction option/cleanup, token-safe owner enforcement and accurate bytes/parser behavior.
- [x] Dedicated source read-only transaction; bounded row/LOB writing with explicit safe limit until verified chunk codecs exist.
- [x] Preserve actual geometry SRID and spatial metadata; reject unsupported cases instead of writing 4326 defaults. Honor generated/identity/selection/options.
- [x] Add tests in existing Oracle smoke files and report coverage/limits; no Oracle server available assumed.

## Task 4: Integration and file/session protection (root)
- [x] Provide common owned job session and atomic publication helpers; integrate only MySQL/SQLite/Oracle native forms and relevant Form1 routes.
- [x] Preserve single-table/data-only routes and wait for worker cleanup on close/cancel.
- [x] Debug/Release builds, full smoke suite, provider regressions and focused independent final review.
- [x] Update truthful support documentation and append history with evidence and unverified boundaries. Retain local repair branch for user review.

## Execution decisions

The accepted review already supplies the design and root causes; no additional design approval is necessary. Independent provider edits are delegated in parallel with disjoint ownership; root alone edits shared routing/project/test registration. Each worker returns a report for scoped review. Native worktree creation failed to resolve this repository from the projectless chat, so git fallback created C:/Users/johnho/Documents/mySQLPunk-worktrees/db-wizard-repairs on codex/db-wizard-repairs. Original feature branches remain intact.

## Execution ledger

- Baseline: Debug application build, all 268 smoke groups and AutoReleasePolicy passed before repairs. Original five recording-provider and eight private SQLite reproductions remain red evidence outside Git.
- Common helpers: new tests observed missing old behavior against the original assembly, then actual-product publication-lock/owned-session rollback/routing tests passed. Single-table defaults were subsequently tightened and covered in both export modes and the SQLite/Oracle routes.
- MySQL: twelve provider groups pass isolated source tests. Independent review found INSERT modifiers, trigger pseudo-row qualifiers and unrelated single-table objects; one correction wave and one scoped re-review addressed all three. Final full suite passed with the integrated source.
- SQLite: sixteen provider/form groups pass isolated tests; fresh actual-product nineteen-group SQLite run plus AutoReleasePolicy passed. Independent review's five transaction/metadata/lifetime/options findings addressed in one wave; scoped re-review approved.
- Oracle: twelve actual-product recording-provider groups plus AutoReleasePolicy passed before final review. Six concrete owner/metadata/LOB-policy/float/view/FK findings addressed in one bounded wave and scoped re-review approved; final targeted Oracle15 and full-suite runs passed.
- Ruling: finite provider row/statement/type gates explicitly reject unverified large/unsupported values before publication rather than truncate or fabricate data. Larger theoretical type maxima and real-server fidelity remain pending; documented caps are visible in errors/UI/support notes.
- Ruling: keep native MySQL source snapshots limited to verified InnoDB, keep Oracle SYS read-only and cross-session-user spatial metadata restrictions explicit, and reject ambiguous SQL scope. Conservative failures can require the native client but preserve data rather than pretending general SQL confinement.

- Final root gate: all 294 smoke groups plus AutoReleasePolicy passed; explicit Debug and Release application builds passed. Optional live tests were skipped. All three independent scoped reviews/re-reviews approved. Existing-file BOM/newlines and diff whitespace were audited; no code edits after final gate.
