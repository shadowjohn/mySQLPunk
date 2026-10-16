# MySQL spatial export repair

Date: 2026-10-16. Baseline: `9f5f3ed82f15f18c277ba094205e9db982df7481` on `codex/db-wizard-repairs`.

## Problem and behavior

The exporter rejected spatial column metadata with `Spatial column values require a native MySQL export`. Because metadata validation occurred immediately before exporting each table, an unsupported table late in the selection could stop a long-running export after earlier tables had been processed. This explains the reported `city_geometry.SHAPE` failure; no source database inspection was performed.

`MySqlSqlExportService.ValidateTableDataCompatibility` now provides a metadata-only check for the wizard before export confirmation. It does not count or scan rows, start transactions, create output, or retain metadata across attempts. Unsupported or absent writable-column type metadata fails explicitly with the table/type context and native-export guidance.

Each actual export independently reloads every selected table's shape. After the optional verified-InnoDB consistent snapshot has started, it first validates all selected table metadata, then establishes all row bounds and counts, and only then creates a staging file or reads data pages. `ReadingCatalog` progress identifies the table being checked. Schema-only export does not require a data compatibility check.

## Spatial representation and bounded operation

The eight supported ordinary spatial types are `GEOMETRY`, `POINT`, `LINESTRING`, `POLYGON`, `MULTIPOINT`, `MULTILINESTRING`, `MULTIPOLYGON`, and `GEOMETRYCOLLECTION`. Writable spatial columns are projected as binary `ST_AsWKB(column)` plus their per-row `ST_SRID(column)`. Output uses `ST_GeomFromWKB(0x...,srid)` with exact hexadecimal WKB and invariant numeric SRID. Zero and nonzero SRIDs are preserved; no SRID is synthesized or transformed.

Both-null WKB/SRID pairs emit SQL `NULL`. Non-null geometry requires nonempty binary WKB and an integral unsigned 32-bit SRID. One-sided nulls, missing projected helpers, empty/nonbinary WKB, fractional/negative/out-of-range SRIDs and unsupported value types fail explicitly.

Generated columns retain their DDL and are omitted before source projection, row-size bounding and INSERT column selection. Helper aliases are allocated against every original column name using case-insensitive comparison. INSERT targets come exclusively from original writable metadata; helper values never become target columns. Existing identifier quoting handles backticks, Unicode and embedded dots.

The existing 1 MiB source-row materialization bound, 16-row page cap, 8 MiB INSERT limit, atomic publication and prior-output preservation remain. Spatial bounds include WKB length and ten conservative SRID bytes. Server-side CASE guards cover both WKB and SRID so enlargement after the initial bound cannot send an oversized geometry page to the client. Hex expansion and constructor overhead count toward the final INSERT limit.

## Compatibility boundary

The common two-argument constructor deliberately pairs the source's default WKB axis convention with the compatible target's default constructor convention. MySQL 8 uses its SRS-defined geographic axis order by default; MariaDB documents `ST_GeomFromWKB(wkb[,srid])`. No unconditional MySQL-only third axis-order argument is emitted.

Supported intent is a compatible-engine, compatible-SRS round trip. Cross-vendor or cross-version geographic migration, coordinate transformations, custom SRS-definition transfer, and unknown target SRS availability are outside this repair. WKB preserves binary coordinates without a WKT decimal conversion, but actual server constructor behavior and spatial fidelity remain to be verified on the user's selected source/target engines. The SQL INSERT cap is independent of a target server's potentially smaller `max_allowed_packet`.

Official references:

- [MySQL spatial formats and internal SRID prefix](https://dev.mysql.com/doc/refman/8.0/en/gis-data-formats.html)
- [MySQL WKB conversion and default axis handling](https://dev.mysql.com/doc/refman/8.0/en/gis-format-conversion-functions.html)
- [MySQL WKB construction, axis order and undefined SRS errors](https://dev.mysql.com/doc/refman/8.0/en/gis-wkb-functions.html)
- [MariaDB two-argument WKB constructor](https://mariadb.com/docs/server/reference/sql-statements/geometry-constructors/wkb/st_geomfromwkb)
- [MySQL spatial upgrade incompatibilities](https://dev.mysql.com/blog-archive/upgrading-to-mysql-8-0-with-spatial-data/)
- [MySQL packet-size limits](https://dev.mysql.com/doc/refman/8.0/en/packet-too-large.html)

## Local evidence

The root integration run `C:/Users/johnho/Documents/mySQLPunk-reviews/2026-10-16/spatial-mysql.log` passed all 27 MySQL smoke groups plus AutoReleasePolicy. The five new recording-provider groups are:

1. `MySqlExport_Geometry_MetadataPreflightAndFreshAttempt`: all eight metadata types, scalar enum containing spatial words, metadata-only work, fresh attempt metadata and cancellation.
2. `MySqlExport_Geometry_ValuesAndImportExpressions`: exact binary WKB, NULL, zero/nonzero/maximum SRIDs, ordinary BLOB values, actual digest/parser preflight, and the actual import runner accepting intact constructor expressions through a recording provider.
3. `MySqlExport_Geometry_GeneratedColumnsAndIdentifiers`: generated geometry DDL, exclusion from scans/INSERTs, multiple geometries, escaped identifiers and case-insensitive helper collisions.
4. `MySqlExport_Geometry_BoundsRaceAndMalformedValues`: malformed WKB/SRID pairs, initial row bound, guarded enlargement race, explicit failure and preservation of the previous complete backup.
5. `MySqlExport_Geometry_BatchCapsAndEarlyFailure`: unsupported last-table rejection before any bounds/counts/pages/staging, whole-selection preflight order, six synthetic near-limit binary projection payloads, 16-row pages, and SQL statements below 8 MiB after hex expansion.

`spatial-values-red.log` verifies that the previous actual Debug binary fails the new spatial-values test with its original spatial-column error. `spatial-ui-red.log` separately records the old wizard advancing unsupported metadata to confirmation. The successful integrated run includes the root-owned UI regressions.

Read-only diff whitespace verification passed for the service. The service and new test file were audited as UTF-8 without BOM, with CRLF and no bare CR/LF. This report uses LF. No other file was edited by this implementation subagent.

## Deferred acceptance

No actual MySQL/MariaDB connection, server startup, saved profile/credential access, user-source scan, real import/restore or production write was performed. The user explicitly deferred real database restoration. Recording-provider completion proves generated-expression/parser/runner behavior, not live geometry fidelity or data landing.

Later authorized private live acceptance should compare restored `ST_SRID`, `HEX(ST_AsWKB(...))`, subtype and asymmetric point coordinates, including geographic SRID 4326, zero/custom SRIDs where supported, empty geometry collections, spatial indexes and generated spatial expressions. Row-count/hash manifests alone do not prove spatial equality. Full-suite/build/review results are maintained by the root in the integration verification document.

Root final gate: complete304 smoke groups and AutoReleasePolicy PASS; explicit default Debug and Release builds PASS. Independent integrated source/test review found no material findings. All twelve modified/new files passed final UTF-8/BOM/newline and whitespace audits. Actual database acceptance remains deferred.
