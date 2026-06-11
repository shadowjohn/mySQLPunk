using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using mySQLPunk;
using mySQLPunk.lib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class SmokeTests
{
    [STAThread]
    public static int Main()
    {
        int passed = 0;
        Run("Geometry WKB 轉 WKT", TestGeometryWktConverter, ref passed);
        Run("View SQL 跨 provider 轉換", TestViewSqlConversion, ref passed);
        Run("View SQL 進階轉換案例", TestAdvancedViewSqlConversion, ref passed);
        Run("SQLite 專用物件 SQL builder", TestSqliteSpecialObjectSqlBuilder, ref passed);
        Run("SQLite 專用物件精靈執行 fallback", TestSqliteSpecialObjectWizardExecutionFallback, ref passed);
        Run("Table Designer DDL builder", TestTableDesignerDdlBuilder, ref passed);
        Run("Table Designer ALTER provider matrix", TestTableDesignerAlterProviderMatrix, ref passed);
        Run("Table Designer comment dictionary diff", TestTableDesignerCommentDictionaryDiff, ref passed);
        Run("Table Designer comment dictionary versions", TestTableDesignerCommentDictionaryVersions, ref passed);
        Run("Pre-delete backup path builder", TestPreDeleteBackupPathBuilder, ref passed);
        Run("Pre-delete backup archive service", TestPreDeleteBackupArchiveService, ref passed);
        Run("Backup restore service", TestBackupRestoreService, ref passed);
        Run("Database dump service", TestDatabaseDumpService, ref passed);
        Run("SQLite column comment exchange service", TestSqliteColumnCommentExchangeService, ref passed);
        Run("SpatiaLite runtime diagnostics", TestSpatiaLiteRuntimeDiagnostics, ref passed);
        Run("Query result export service", TestQueryResultExportService, ref passed);
        Run("Query form option settings", TestQueryFormOptionSettings, ref passed);
        Run("Query table edit optimistic WHERE", TestQueryTableEditOptimisticWhere, ref passed);
        Run("Dockable tab option service", TestDockableTabOptionService, ref passed);
        Run("Auto recovery draft service", TestAutoRecoveryDraftService, ref passed);
        Run("Diagnostic log service", TestDiagnosticLogService, ref passed);
        Run("Data view filter service", TestDataViewFilterService, ref passed);
        Run("Data view sort service", TestDataViewSortService, ref passed);
        Run("Database group visibility service", TestDatabaseGroupVisibilityService, ref passed);
        Run("View column preference service", TestViewColumnPreferenceService, ref passed);
        Run("Binary cell streaming service", TestBinaryCellStreamingService, ref passed);
        Run("Connection and metadata services", TestConnectionAndMetadataServices, ref passed);
        Run("Connection editor localization", TestConnectionEditorLocalization, ref passed);
        Run("Provider SQL 執行 fallback", TestDatabaseExecutionResultService, ref passed);
        Run("Connection profile service", TestConnectionProfileService, ref passed);
        Run("MySQL GuidFormat 預設關閉", TestMySqlGuidFormatNone, ref passed);
        Run("Connection proxy settings service", TestConnectionProxySettingsService, ref passed);
        Run("Advanced registration service", TestAdvancedRegistrationService, ref passed);
        Run("Application about message", TestApplicationAboutMessage, ref passed);
        Run("Application update check service", TestApplicationUpdateCheckService, ref passed);
        Run("Release packaging script", TestReleasePackagingScript, ref passed);
        Run("Release third-party notices", TestReleaseThirdPartyNotices, ref passed);
        Run("GitHub release workflow", TestGitHubReleaseWorkflow, ref passed);
        Run("Dark theme control coverage", TestDarkThemeControlCoverage, ref passed);
        Run("Connection export signature helpers", TestConnectionExportSignatureHelpers, ref passed);
        Run("Connection import password helpers", TestConnectionImportPasswordHelpers, ref passed);
        Run("Windows credential service", TestWindowsCredentialService, ref passed);
        Console.WriteLine("Smoke tests passed: " + passed);
        return 0;
    }

    private static void Run(string name, Action test, ref int passed)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine("[PASS] " + name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + name);
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
        }
    }

    private static void TestGeometryWktConverter()
    {
        byte[] wkb = BuildLittleEndianPointWkb(121.5, 25);
        string wkt;
        Assert(GeometryWktConverter.TryGeometryBytesToWkt(wkb, out wkt), "WKB point should convert to WKT.");
        AssertEquals("POINT (121.5 25)", wkt, "Unexpected WKT point output.");

        Assert(!GeometryWktConverter.TryGeometryBytesToWkt(new byte[] { 1, 2, 3, 4 }, out wkt), "Invalid geometry bytes should fail cleanly.");

        Type readerType = typeof(GeometryWktConverter).GetNestedType("WkbReader", BindingFlags.NonPublic);
        MethodInfo readGeometryMethod = readerType.GetMethod("ReadGeometry", BindingFlags.Instance | BindingFlags.Public);
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            object invalidEndianReader = Activator.CreateInstance(readerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { new byte[] { 2, 1, 0, 0, 0 }, 0 }, null);
            try
            {
                readGeometryMethod.Invoke(invalidEndianReader, new object[0]);
                Assert(false, "Invalid WKB byte order should throw.");
            }
            catch (TargetInvocationException ex)
            {
                FormatException formatException = ex.InnerException as FormatException;
                Assert(formatException != null, "Invalid WKB byte order should throw FormatException.");
                AssertContains(formatException.Message, "WKB byte order 無效", "WKB byte-order errors should localize Traditional Chinese messages.");
            }

            Localization.SetLanguage(Localization.English, false);
            object unsupportedTypeReader = Activator.CreateInstance(readerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { new byte[] { 1, 8, 0, 0, 0 }, 0 }, null);
            try
            {
                readGeometryMethod.Invoke(unsupportedTypeReader, new object[0]);
                Assert(false, "Unsupported WKB geometry type should throw.");
            }
            catch (TargetInvocationException ex)
            {
                FormatException formatException = ex.InnerException as FormatException;
                Assert(formatException != null, "Unsupported WKB geometry type should throw FormatException.");
                AssertContains(formatException.Message, "Unsupported WKB geometry type", "Unsupported WKB type errors should localize English messages.");
            }

            object truncatedReader = Activator.CreateInstance(readerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { new byte[] { 1, 1, 0 }, 0 }, null);
            try
            {
                readGeometryMethod.Invoke(truncatedReader, new object[0]);
                Assert(false, "Truncated WKB should throw.");
            }
            catch (TargetInvocationException ex)
            {
                FormatException formatException = ex.InnerException as FormatException;
                Assert(formatException != null, "Truncated WKB should throw FormatException.");
                AssertContains(formatException.Message, "Unexpected end of WKB", "Truncated WKB errors should localize English messages.");
            }
        }
        finally
        {
            Localization.SetLanguage(previousLanguage, false);
        }
    }

    private static byte[] BuildLittleEndianPointWkb(double x, double y)
    {
        byte[] bytes = new byte[21];
        bytes[0] = 1;
        Array.Copy(BitConverter.GetBytes((uint)1), 0, bytes, 1, 4);
        Array.Copy(BitConverter.GetBytes(x), 0, bytes, 5, 8);
        Array.Copy(BitConverter.GetBytes(y), 0, bytes, 13, 8);
        return bytes;
    }

    private static void TestViewSqlConversion()
    {
        object mysqlPreview = BuildViewSqlPreview(
            "CREATE VIEW v AS SELECT TOP (5) Id, GETDATE() AS CheckedAt FROM dbo.Users",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlPreview, "CanConvert"), "SQL Server TOP query should convert to MySQL.");
        string mysqlSql = (string)GetProperty(mysqlPreview, "ConvertedSql");
        AssertContains(mysqlSql, "LIMIT 5", "Converted MySQL SQL should contain LIMIT.");
        AssertContains(mysqlSql, "NOW()", "Converted MySQL SQL should rewrite GETDATE().");

        object mssqlPreview = BuildViewSqlPreview(
            "SELECT id, DATE_FORMAT(created_at, '%Y-%m-%d') AS day_text FROM logs LIMIT 10",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlPreview, "CanConvert"), "MySQL LIMIT query should convert to SQL Server.");
        string mssqlSql = (string)GetProperty(mssqlPreview, "ConvertedSql");
        AssertContains(mssqlSql, "TOP (10)", "Converted SQL Server SQL should contain TOP.");
        AssertContains(mssqlSql, "FORMAT(", "Converted SQL Server SQL should rewrite DATE_FORMAT.");

        object mssqlOffsetPreview = BuildViewSqlPreview(
            "SELECT id, created_at FROM logs ORDER BY created_at DESC LIMIT 10 OFFSET 20",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlOffsetPreview, "CanConvert"), "Ordered MySQL LIMIT OFFSET query should convert to SQL Server.");
        string mssqlOffsetSql = (string)GetProperty(mssqlOffsetPreview, "ConvertedSql");
        AssertContains(mssqlOffsetSql, "ORDER BY created_at DESC OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY", "Converted SQL Server SQL should use OFFSET FETCH.");

        object mssqlUnsafeOffsetPreview = BuildViewSqlPreview(
            "SELECT id, created_at FROM logs LIMIT 10 OFFSET 20",
            "mysql",
            "mssql");
        Assert(!(bool)GetProperty(mssqlUnsafeOffsetPreview, "CanConvert"), "Unordered MySQL LIMIT OFFSET query should still be rejected for SQL Server.");
        string unsafeOffsetReason = (string)GetProperty(mssqlUnsafeOffsetPreview, "Reason");
        AssertContains(unsafeOffsetReason, "ORDER BY", "Unsafe offset rejection should explain the missing ORDER BY.");

        object mysqlOffsetFetchPreview = BuildViewSqlPreview(
            "SELECT id, created_at FROM logs ORDER BY created_at DESC OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlOffsetFetchPreview, "CanConvert"), "SQL Server OFFSET FETCH should convert to MySQL.");
        string mysqlOffsetFetchSql = (string)GetProperty(mysqlOffsetFetchPreview, "ConvertedSql");
        AssertContains(mysqlOffsetFetchSql, "ORDER BY created_at DESC LIMIT 10 OFFSET 20", "Converted MySQL SQL should use LIMIT OFFSET.");

        object pgOracleOffsetFetchPreview = BuildViewSqlPreview(
            "SELECT id, created_at FROM logs ORDER BY created_at DESC OFFSET 5 ROWS FETCH FIRST 15 ROWS ONLY",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgOracleOffsetFetchPreview, "CanConvert"), "Oracle OFFSET FETCH should convert to PostgreSQL.");
        string pgOracleOffsetFetchSql = (string)GetProperty(pgOracleOffsetFetchPreview, "ConvertedSql");
        AssertContains(pgOracleOffsetFetchSql, "ORDER BY created_at DESC LIMIT 15 OFFSET 5", "Converted PostgreSQL SQL should use LIMIT OFFSET.");

        object mssqlOffsetFetchPreview = BuildViewSqlPreview(
            "SELECT id, created_at FROM logs ORDER BY created_at DESC OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlOffsetFetchPreview, "CanConvert"), "SQL Server target should keep OFFSET FETCH syntax.");
        string mssqlOffsetFetchSql = (string)GetProperty(mssqlOffsetFetchPreview, "ConvertedSql");
        AssertContains(mssqlOffsetFetchSql, "OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY", "Converted SQL Server SQL should keep OFFSET FETCH.");

        object mysqlFetchFirstPreview = BuildViewSqlPreview(
            "SELECT id, created_at FROM logs ORDER BY created_at DESC FETCH FIRST 12 ROWS ONLY",
            "oracle",
            "mysql");
        Assert((bool)GetProperty(mysqlFetchFirstPreview, "CanConvert"), "Oracle FETCH FIRST should convert to MySQL.");
        string mysqlFetchFirstSql = (string)GetProperty(mysqlFetchFirstPreview, "ConvertedSql");
        AssertContains(mysqlFetchFirstSql, "ORDER BY created_at DESC LIMIT 12", "Converted MySQL SQL should use LIMIT for FETCH FIRST.");
        AssertNotContains(mysqlFetchFirstSql, "FETCH FIRST", "Converted MySQL SQL should remove FETCH FIRST.");

        object mssqlFetchFirstPreview = BuildViewSqlPreview(
            "SELECT id, created_at FROM logs FETCH FIRST 7 ROWS ONLY",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlFetchFirstPreview, "CanConvert"), "PostgreSQL FETCH FIRST should convert to SQL Server.");
        string mssqlFetchFirstSql = (string)GetProperty(mssqlFetchFirstPreview, "ConvertedSql");
        AssertContains(mssqlFetchFirstSql, "SELECT TOP (7) id, created_at FROM logs", "Converted SQL Server SQL should use TOP for FETCH FIRST.");
    }

    private static void TestAdvancedViewSqlConversion()
    {
        object pgPreview = BuildViewSqlPreview(
            "SELECT id, name FROM accounts WHERE is_active = 1 AND ROWNUM <= 3",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgPreview, "CanConvert"), "Simple Oracle ROWNUM predicate should convert to PostgreSQL.");
        string pgSql = (string)GetProperty(pgPreview, "ConvertedSql");
        AssertContains(pgSql, "LIMIT 3", "Converted PostgreSQL SQL should contain LIMIT from ROWNUM.");
        AssertNotContains(pgSql, "ROWNUM", "Converted PostgreSQL SQL should remove simple ROWNUM predicate.");

        object aggregatePreview = BuildViewSqlPreview(
            "SELECT project_id, GROUP_CONCAT(user_name SEPARATOR '|') AS members FROM project_users GROUP BY project_id",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(aggregatePreview, "CanConvert"), "MySQL GROUP_CONCAT should convert to SQL Server.");
        string aggregateSql = (string)GetProperty(aggregatePreview, "ConvertedSql");
        AssertContains(aggregateSql, "STRING_AGG(user_name, '|')", "Converted SQL Server SQL should use STRING_AGG.");

        object jsonPreview = BuildViewSqlPreview(
            "SELECT JSON_EXTRACT(payload, '$.status') AS status_text FROM event_log",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(jsonPreview, "CanConvert"), "MySQL JSON_EXTRACT should convert to Oracle.");
        string jsonSql = (string)GetProperty(jsonPreview, "ConvertedSql");
        AssertContains(jsonSql, "JSON_VALUE(payload, '$.status')", "Converted Oracle SQL should use JSON_VALUE.");

        object pgJsonPreview = BuildViewSqlPreview(
            "SELECT JSON_EXTRACT(payload, '$.user.name') AS user_name FROM event_log",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgJsonPreview, "CanConvert"), "MySQL JSON_EXTRACT should convert to PostgreSQL.");
        string pgJsonSql = (string)GetProperty(pgJsonPreview, "ConvertedSql");
        AssertContains(pgJsonSql, "payload #>> '{user,name}'", "Converted PostgreSQL SQL should use text JSON path extraction.");

        object pgJsonUnquoteExtractPreview = BuildViewSqlPreview(
            "SELECT JSON_UNQUOTE(JSON_EXTRACT(payload, '$.user.name')) AS user_name FROM event_log",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgJsonUnquoteExtractPreview, "CanConvert"), "MySQL JSON_UNQUOTE(JSON_EXTRACT) should convert to PostgreSQL.");
        string pgJsonUnquoteExtractSql = (string)GetProperty(pgJsonUnquoteExtractPreview, "ConvertedSql");
        AssertContains(pgJsonUnquoteExtractSql, "payload #>> '{user,name}'", "Converted PostgreSQL SQL should use text JSON path extraction for JSON_UNQUOTE(JSON_EXTRACT).");
        AssertNotContains(pgJsonUnquoteExtractSql, "JSON_UNQUOTE", "Converted PostgreSQL SQL should remove JSON_UNQUOTE.");

        object mssqlJsonUnquoteExtractPreview = BuildViewSqlPreview(
            "SELECT JSON_UNQUOTE(JSON_EXTRACT(payload, '$.status')) AS status_text FROM event_log",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlJsonUnquoteExtractPreview, "CanConvert"), "MySQL JSON_UNQUOTE(JSON_EXTRACT) should convert to SQL Server.");
        string mssqlJsonUnquoteExtractSql = (string)GetProperty(mssqlJsonUnquoteExtractPreview, "ConvertedSql");
        AssertContains(mssqlJsonUnquoteExtractSql, "JSON_VALUE(payload, '$.status')", "Converted SQL Server SQL should use JSON_VALUE for JSON_UNQUOTE(JSON_EXTRACT).");
        AssertNotContains(mssqlJsonUnquoteExtractSql, "JSON_UNQUOTE", "Converted SQL Server SQL should remove JSON_UNQUOTE.");

        object oracleMySqlJsonArrowTextPreview = BuildViewSqlPreview(
            "SELECT payload ->> '$.user.name' AS user_name FROM event_log",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleMySqlJsonArrowTextPreview, "CanConvert"), "MySQL JSON ->> path operator should convert to Oracle.");
        string oracleMySqlJsonArrowTextSql = (string)GetProperty(oracleMySqlJsonArrowTextPreview, "ConvertedSql");
        AssertContains(oracleMySqlJsonArrowTextSql, "JSON_VALUE(payload, '$.user.name')", "Converted Oracle SQL should use JSON_VALUE for MySQL ->>.");
        AssertNotContains(oracleMySqlJsonArrowTextSql, "->>", "Converted Oracle SQL should remove MySQL JSON ->> operator.");

        object pgMySqlJsonArrowFragmentPreview = BuildViewSqlPreview(
            "SELECT payload -> '$.items[0]' AS first_item FROM event_log",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgMySqlJsonArrowFragmentPreview, "CanConvert"), "MySQL JSON -> path operator should convert to PostgreSQL.");
        string pgMySqlJsonArrowFragmentSql = (string)GetProperty(pgMySqlJsonArrowFragmentPreview, "ConvertedSql");
        AssertContains(pgMySqlJsonArrowFragmentSql, "payload #> '{items,0}'", "Converted PostgreSQL SQL should use JSON path extraction for MySQL ->.");
        AssertNotContains(pgMySqlJsonArrowFragmentSql, "->", "Converted PostgreSQL SQL should remove MySQL JSON -> operator.");

        object mysqlJsonValuePreview = BuildViewSqlPreview(
            "SELECT JSON_VALUE(payload, '$.status') AS status_text FROM event_log",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlJsonValuePreview, "CanConvert"), "SQL Server JSON_VALUE should convert to MySQL.");
        string mysqlJsonValueSql = (string)GetProperty(mysqlJsonValuePreview, "ConvertedSql");
        AssertContains(mysqlJsonValueSql, "JSON_UNQUOTE(JSON_EXTRACT(payload, '$.status'))", "Converted MySQL SQL should use JSON_EXTRACT with unquote.");

        object mysqlJsonQueryPreview = BuildViewSqlPreview(
            "SELECT JSON_QUERY(payload, '$.items') AS items_json FROM event_log",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlJsonQueryPreview, "CanConvert"), "SQL Server JSON_QUERY should convert to MySQL.");
        string mysqlJsonQuerySql = (string)GetProperty(mysqlJsonQueryPreview, "ConvertedSql");
        AssertContains(mysqlJsonQuerySql, "JSON_EXTRACT(payload, '$.items')", "Converted MySQL SQL should use JSON_EXTRACT for JSON fragments.");

        object pgJsonQueryPreview = BuildViewSqlPreview(
            "SELECT JSON_QUERY(payload, '$.items[0]') AS first_item_json FROM event_log",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgJsonQueryPreview, "CanConvert"), "SQL Server JSON_QUERY should convert to PostgreSQL.");
        string pgJsonQuerySql = (string)GetProperty(pgJsonQueryPreview, "ConvertedSql");
        AssertContains(pgJsonQuerySql, "payload #> '{items,0}'", "Converted PostgreSQL SQL should use JSON path extraction for JSON fragments.");

        object sqliteJsonQueryPreview = BuildViewSqlPreview(
            "SELECT JSON_QUERY(payload, '$.items') AS items_json FROM event_log",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteJsonQueryPreview, "CanConvert"), "Oracle JSON_QUERY should convert to SQLite.");
        string sqliteJsonQuerySql = (string)GetProperty(sqliteJsonQueryPreview, "ConvertedSql");
        AssertContains(sqliteJsonQuerySql, "json_extract(payload, '$.items')", "Converted SQLite SQL should use json_extract for JSON fragments.");

        object mysqlPgJsonTextPreview = BuildViewSqlPreview(
            "SELECT payload ->> 'status' AS status_text FROM event_log",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlPgJsonTextPreview, "CanConvert"), "PostgreSQL JSON text operator should convert to MySQL.");
        string mysqlPgJsonTextSql = (string)GetProperty(mysqlPgJsonTextPreview, "ConvertedSql");
        AssertContains(mysqlPgJsonTextSql, "JSON_UNQUOTE(JSON_EXTRACT(payload, '$.status'))", "Converted MySQL SQL should use JSON_EXTRACT with unquote for ->>.");

        object oraclePgJsonPathPreview = BuildViewSqlPreview(
            "SELECT payload #>> '{user,name}' AS user_name FROM event_log",
            "postgresql",
            "oracle");
        Assert((bool)GetProperty(oraclePgJsonPathPreview, "CanConvert"), "PostgreSQL JSON path text operator should convert to Oracle.");
        string oraclePgJsonPathSql = (string)GetProperty(oraclePgJsonPathPreview, "ConvertedSql");
        AssertContains(oraclePgJsonPathSql, "JSON_VALUE(payload, '$.user.name')", "Converted Oracle SQL should use JSON_VALUE for #>>.");

        object sqlitePgJsonArrayPreview = BuildViewSqlPreview(
            "SELECT payload #> '{items,0}' AS first_item FROM event_log",
            "postgresql",
            "sqlite");
        Assert((bool)GetProperty(sqlitePgJsonArrayPreview, "CanConvert"), "PostgreSQL JSON path fragment operator should convert to SQLite.");
        string sqlitePgJsonArraySql = (string)GetProperty(sqlitePgJsonArrayPreview, "ConvertedSql");
        AssertContains(sqlitePgJsonArraySql, "json_extract(payload, '$.items[0]')", "Converted SQLite SQL should use json_extract for #> array paths.");

        object mysqlJsonExistsPreview = BuildViewSqlPreview(
            "SELECT JSON_EXISTS(payload, '$.items') AS has_items FROM event_log",
            "oracle",
            "mysql");
        Assert((bool)GetProperty(mysqlJsonExistsPreview, "CanConvert"), "Oracle JSON_EXISTS should convert to MySQL.");
        string mysqlJsonExistsSql = (string)GetProperty(mysqlJsonExistsPreview, "ConvertedSql");
        AssertContains(mysqlJsonExistsSql, "JSON_CONTAINS_PATH(payload, 'one', '$.items')", "Converted MySQL SQL should use JSON_CONTAINS_PATH.");

        object pgJsonContainsPathPreview = BuildViewSqlPreview(
            "SELECT JSON_CONTAINS_PATH(payload, 'one', '$.items') AS has_items FROM event_log",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgJsonContainsPathPreview, "CanConvert"), "MySQL JSON_CONTAINS_PATH should convert to PostgreSQL.");
        string pgJsonContainsPathSql = (string)GetProperty(pgJsonContainsPathPreview, "ConvertedSql");
        AssertContains(pgJsonContainsPathSql, "(payload::jsonb #> '{items}') IS NOT NULL", "Converted PostgreSQL SQL should test JSON path presence.");
        AssertNotContains(pgJsonContainsPathSql, "JSON_CONTAINS_PATH", "Converted PostgreSQL SQL should remove JSON_CONTAINS_PATH.");

        object sqliteJsonExistsPreview = BuildViewSqlPreview(
            "SELECT JSON_EXISTS(payload, '$.items[0]') AS has_first_item FROM event_log",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteJsonExistsPreview, "CanConvert"), "Oracle JSON_EXISTS should convert to SQLite.");
        string sqliteJsonExistsSql = (string)GetProperty(sqliteJsonExistsPreview, "ConvertedSql");
        AssertContains(sqliteJsonExistsSql, "json_type(payload, '$.items[0]') IS NOT NULL", "Converted SQLite SQL should use json_type for path presence.");

        object mssqlJsonExistsPreview = BuildViewSqlPreview(
            "SELECT JSON_EXISTS(payload, '$.items') AS has_items FROM event_log",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlJsonExistsPreview, "CanConvert"), "Oracle JSON_EXISTS should convert to SQL Server.");
        string mssqlJsonExistsSql = (string)GetProperty(mssqlJsonExistsPreview, "ConvertedSql");
        AssertContains(mssqlJsonExistsSql, "(JSON_VALUE(payload, '$.items') IS NOT NULL OR JSON_QUERY(payload, '$.items') IS NOT NULL)", "Converted SQL Server SQL should test scalar or fragment JSON path presence.");

        object oracleJsonContainsPathPreview = BuildViewSqlPreview(
            "SELECT JSON_CONTAINS_PATH(payload, 'one', '$.items') AS has_items FROM event_log",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleJsonContainsPathPreview, "CanConvert"), "MySQL JSON_CONTAINS_PATH should convert to Oracle.");
        string oracleJsonContainsPathSql = (string)GetProperty(oracleJsonContainsPathPreview, "ConvertedSql");
        AssertContains(oracleJsonContainsPathSql, "JSON_EXISTS(payload, '$.items')", "Converted Oracle SQL should use JSON_EXISTS.");

        object mysqlJsonArrayLengthPreview = BuildViewSqlPreview(
            "SELECT JSON_ARRAY_LENGTH(payload, '$.items') AS item_count FROM event_log",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlJsonArrayLengthPreview, "CanConvert"), "PostgreSQL JSON_ARRAY_LENGTH should convert to MySQL.");
        string mysqlJsonArrayLengthSql = (string)GetProperty(mysqlJsonArrayLengthPreview, "ConvertedSql");
        AssertContains(mysqlJsonArrayLengthSql, "JSON_LENGTH(payload, '$.items')", "Converted MySQL SQL should use JSON_LENGTH.");

        object pgJsonLengthPreview = BuildViewSqlPreview(
            "SELECT JSON_LENGTH(payload, '$.items') AS item_count FROM event_log",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgJsonLengthPreview, "CanConvert"), "MySQL JSON_LENGTH should convert to PostgreSQL.");
        string pgJsonLengthSql = (string)GetProperty(pgJsonLengthPreview, "ConvertedSql");
        AssertContains(pgJsonLengthSql, "jsonb_array_length(payload::jsonb #> '{items}')", "Converted PostgreSQL SQL should use jsonb_array_length.");
        AssertNotContains(pgJsonLengthSql, "JSON_LENGTH", "Converted PostgreSQL SQL should remove JSON_LENGTH.");

        object mssqlJsonLengthPreview = BuildViewSqlPreview(
            "SELECT JSON_ARRAY_LENGTH(payload, '$.items') AS item_count FROM event_log",
            "sqlite",
            "mssql");
        Assert((bool)GetProperty(mssqlJsonLengthPreview, "CanConvert"), "SQLite JSON_ARRAY_LENGTH should convert to SQL Server.");
        string mssqlJsonLengthSql = (string)GetProperty(mssqlJsonLengthPreview, "ConvertedSql");
        AssertContains(mssqlJsonLengthSql, "(SELECT COUNT(*) FROM OPENJSON(payload, '$.items'))", "Converted SQL Server SQL should count OPENJSON rows.");

        object oracleJsonLengthPreview = BuildViewSqlPreview(
            "SELECT JSON_LENGTH(payload, '$.items') AS item_count FROM event_log",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleJsonLengthPreview, "CanConvert"), "MySQL JSON_LENGTH should convert to Oracle.");
        string oracleJsonLengthSql = (string)GetProperty(oracleJsonLengthPreview, "ConvertedSql");
        AssertContains(oracleJsonLengthSql, "JSON_VALUE(payload, '$.items.size()' RETURNING NUMBER)", "Converted Oracle SQL should use JSON path size().");

        object sqliteJsonLengthPreview = BuildViewSqlPreview(
            "SELECT JSON_LENGTH(payload, '$.items') AS item_count FROM event_log",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteJsonLengthPreview, "CanConvert"), "MySQL JSON_LENGTH should convert to SQLite.");
        string sqliteJsonLengthSql = (string)GetProperty(sqliteJsonLengthPreview, "ConvertedSql");
        AssertContains(sqliteJsonLengthSql, "json_array_length(payload, '$.items')", "Converted SQLite SQL should use json_array_length.");

        object pgJsonConstructorPreview = BuildViewSqlPreview(
            "SELECT JSON_OBJECT('id', id, 'name', user_name) AS payload, JSON_ARRAY(id, user_name) AS tuple_json FROM users",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgJsonConstructorPreview, "CanConvert"), "MySQL JSON constructors should convert to PostgreSQL.");
        string pgJsonConstructorSql = (string)GetProperty(pgJsonConstructorPreview, "ConvertedSql");
        AssertContains(pgJsonConstructorSql, "jsonb_build_object('id', id, 'name', user_name)", "Converted PostgreSQL SQL should use jsonb_build_object.");
        AssertContains(pgJsonConstructorSql, "jsonb_build_array(id, user_name)", "Converted PostgreSQL SQL should use jsonb_build_array.");
        AssertNotContains(pgJsonConstructorSql, "JSON_OBJECT(", "Converted PostgreSQL SQL should remove JSON_OBJECT.");

        object mysqlJsonConstructorPreview = BuildViewSqlPreview(
            "SELECT jsonb_build_object('id', id, 'name', user_name) AS payload, jsonb_build_array(id, user_name) AS tuple_json FROM users",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlJsonConstructorPreview, "CanConvert"), "PostgreSQL JSON constructors should convert to MySQL.");
        string mysqlJsonConstructorSql = (string)GetProperty(mysqlJsonConstructorPreview, "ConvertedSql");
        AssertContains(mysqlJsonConstructorSql, "JSON_OBJECT('id', id, 'name', user_name)", "Converted MySQL SQL should use JSON_OBJECT.");
        AssertContains(mysqlJsonConstructorSql, "JSON_ARRAY(id, user_name)", "Converted MySQL SQL should use JSON_ARRAY.");
        AssertNotContains(mysqlJsonConstructorSql, "jsonb_build_object", "Converted MySQL SQL should remove jsonb_build_object.");

        object sqliteJsonConstructorPreview = BuildViewSqlPreview(
            "SELECT JSON_OBJECT('id', id, 'name', user_name) AS payload, JSON_ARRAY(id, user_name) AS tuple_json FROM users",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteJsonConstructorPreview, "CanConvert"), "MySQL JSON constructors should convert to SQLite.");
        string sqliteJsonConstructorSql = (string)GetProperty(sqliteJsonConstructorPreview, "ConvertedSql");
        AssertContains(sqliteJsonConstructorSql, "json_object('id', id, 'name', user_name)", "Converted SQLite SQL should use json_object.");
        AssertContains(sqliteJsonConstructorSql, "json_array(id, user_name)", "Converted SQLite SQL should use json_array.");

        object oracleJsonConstructorPreview = BuildViewSqlPreview(
            "SELECT JSON_OBJECT('id', id, 'name', user_name) AS payload, JSON_ARRAY(id, user_name) AS tuple_json FROM users",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleJsonConstructorPreview, "CanConvert"), "MySQL JSON constructors should convert to Oracle.");
        string oracleJsonConstructorSql = (string)GetProperty(oracleJsonConstructorPreview, "ConvertedSql");
        AssertContains(oracleJsonConstructorSql, "JSON_OBJECT(KEY 'id' VALUE id, KEY 'name' VALUE user_name)", "Converted Oracle SQL should use KEY VALUE JSON_OBJECT syntax.");
        AssertContains(oracleJsonConstructorSql, "JSON_ARRAY(id, user_name)", "Converted Oracle SQL should keep JSON_ARRAY.");

        object pgJsonTablePreview = BuildViewSqlPreview(
            "SELECT jt.item_id, jt.item_name FROM orders o CROSS JOIN JSON_TABLE(o.payload, '$.items[*]' COLUMNS (item_id INT PATH '$.id', item_name VARCHAR(80) PATH '$.name')) AS jt",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgJsonTablePreview, "CanConvert"), "MySQL JSON_TABLE should convert to PostgreSQL.");
        string pgJsonTableSql = (string)GetProperty(pgJsonTablePreview, "ConvertedSql");
        AssertContains(pgJsonTableSql, "jsonb_array_elements(o.payload::jsonb #> '{items}') AS json_item(value)", "Converted PostgreSQL SQL should expand JSON arrays with jsonb_array_elements.");
        AssertContains(pgJsonTableSql, "CAST(json_item.value #>> '{id}' AS integer) AS item_id", "Converted PostgreSQL SQL should honor JSON_TABLE column PATH for numeric aliases.");
        AssertContains(pgJsonTableSql, "json_item.value #>> '{name}' AS item_name", "Converted PostgreSQL SQL should honor JSON_TABLE column PATH for text aliases.");
        AssertNotContains(pgJsonTableSql, "JSON_TABLE", "Converted PostgreSQL SQL should remove JSON_TABLE.");

        object mssqlJsonTablePreview = BuildViewSqlPreview(
            "SELECT jt.item_id, jt.qty FROM orders o CROSS JOIN JSON_TABLE(o.payload, '$.items[*]' COLUMNS (item_id INTEGER PATH '$.id', qty DECIMAL(10,2) PATH '$.qty')) jt",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlJsonTablePreview, "CanConvert"), "Oracle JSON_TABLE should convert to SQL Server.");
        string mssqlJsonTableSql = (string)GetProperty(mssqlJsonTablePreview, "ConvertedSql");
        AssertContains(mssqlJsonTableSql, "OPENJSON(o.payload, '$.items') WITH (item_id int '$.id', qty decimal(10,2) '$.qty') AS jt", "Converted SQL Server SQL should use OPENJSON WITH.");
        AssertNotContains(mssqlJsonTableSql, "JSON_TABLE", "Converted SQL Server SQL should remove JSON_TABLE.");

        object sqliteJsonTablePreview = BuildViewSqlPreview(
            "SELECT jt.item_id, 'jt.item_id' AS literal_name FROM orders o CROSS JOIN JSON_TABLE(o.payload, '$.items[*]' COLUMNS (item_id INT PATH '$.id')) AS jt WHERE jt.item_id > 0",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteJsonTablePreview, "CanConvert"), "MySQL JSON_TABLE should convert to SQLite.");
        string sqliteJsonTableSql = (string)GetProperty(sqliteJsonTablePreview, "ConvertedSql");
        AssertContains(sqliteJsonTableSql, "json_each(o.payload, '$.items') AS jt", "Converted SQLite SQL should use json_each for JSON array rows.");
        AssertContains(sqliteJsonTableSql, "CAST(json_extract(jt.value, '$.id') AS INTEGER)", "Converted SQLite SQL should rewrite JSON_TABLE column references to json_extract.");
        AssertContains(sqliteJsonTableSql, "'jt.item_id' AS literal_name", "Converted SQLite SQL should not rewrite string literals that look like JSON_TABLE references.");
        AssertNotContains(sqliteJsonTableSql, "JSON_TABLE", "Converted SQLite SQL should remove JSON_TABLE.");

        object pgJsonTableOrdinalityPreview = BuildViewSqlPreview(
            "SELECT jt.seq_no, jt.item_name FROM orders o CROSS JOIN JSON_TABLE(o.payload, '$.items[*]' COLUMNS (seq_no FOR ORDINALITY, item_name VARCHAR(80) PATH '$.name')) AS jt",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgJsonTableOrdinalityPreview, "CanConvert"), "MySQL JSON_TABLE FOR ORDINALITY should convert to PostgreSQL.");
        string pgJsonTableOrdinalitySql = (string)GetProperty(pgJsonTableOrdinalityPreview, "ConvertedSql");
        AssertContains(pgJsonTableOrdinalitySql, "WITH ORDINALITY AS json_item(value, ordinality)", "Converted PostgreSQL SQL should preserve JSON_TABLE ordinality.");
        AssertContains(pgJsonTableOrdinalitySql, "json_item.ordinality AS seq_no", "Converted PostgreSQL SQL should project ordinality columns.");
        AssertContains(pgJsonTableOrdinalitySql, "json_item.value #>> '{name}' AS item_name", "Converted PostgreSQL SQL should still project PATH columns with ordinality.");
        AssertNotContains(pgJsonTableOrdinalitySql, "JSON_TABLE", "Converted PostgreSQL SQL should remove JSON_TABLE with ordinality.");

        object sqliteJsonTableOrdinalityPreview = BuildViewSqlPreview(
            "SELECT jt.seq_no, jt.item_id FROM orders o CROSS JOIN JSON_TABLE(o.payload, '$.items[*]' COLUMNS (seq_no FOR ORDINALITY, item_id INT PATH '$.id')) AS jt WHERE jt.seq_no > 1",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteJsonTableOrdinalityPreview, "CanConvert"), "MySQL JSON_TABLE FOR ORDINALITY should convert to SQLite.");
        string sqliteJsonTableOrdinalitySql = (string)GetProperty(sqliteJsonTableOrdinalityPreview, "ConvertedSql");
        AssertContains(sqliteJsonTableOrdinalitySql, "(CAST(jt.key AS INTEGER) + 1)", "Converted SQLite SQL should derive one-based ordinality from json_each key.");
        AssertContains(sqliteJsonTableOrdinalitySql, "CAST(json_extract(jt.value, '$.id') AS INTEGER)", "Converted SQLite SQL should keep PATH column rewrite with ordinality.");
        AssertNotContains(sqliteJsonTableOrdinalitySql, "jt.seq_no", "Converted SQLite SQL should rewrite ordinality column references.");
        AssertNotContains(sqliteJsonTableOrdinalitySql, "JSON_TABLE", "Converted SQLite SQL should remove JSON_TABLE with ordinality.");

        object mssqlJsonTableOrdinalityPreview = BuildViewSqlPreview(
            "SELECT jt.seq_no FROM orders o CROSS JOIN JSON_TABLE(o.payload, '$.items[*]' COLUMNS (seq_no FOR ORDINALITY)) jt",
            "mysql",
            "mssql");
        Assert(!(bool)GetProperty(mssqlJsonTableOrdinalityPreview, "CanConvert"), "JSON_TABLE FOR ORDINALITY should be rejected for SQL Server until OPENJSON ordinality is safely modeled.");
        string mssqlJsonTableOrdinalityReason = (string)GetProperty(mssqlJsonTableOrdinalityPreview, "Reason");
        AssertContains(mssqlJsonTableOrdinalityReason, "JSON_TABLE", "Unsupported SQL Server ordinality conversion should explain JSON_TABLE fallback.");

        object sqliteNowPreview = BuildViewSqlPreview(
            "SELECT NOW() AS created_at FROM users",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteNowPreview, "CanConvert"), "MySQL NOW should convert to SQLite.");
        string sqliteNowSql = (string)GetProperty(sqliteNowPreview, "ConvertedSql");
        AssertContains(sqliteNowSql, "CURRENT_TIMESTAMP", "Converted SQLite SQL should use CURRENT_TIMESTAMP.");
        AssertNotContains(sqliteNowSql, "NOW()", "Converted SQLite SQL should remove NOW().");

        object mysqlUtcTimestampPreview = BuildViewSqlPreview(
            "SELECT GETUTCDATE() AS synced_at FROM audit_log",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlUtcTimestampPreview, "CanConvert"), "SQL Server GETUTCDATE should convert to MySQL.");
        string mysqlUtcTimestampSql = (string)GetProperty(mysqlUtcTimestampPreview, "ConvertedSql");
        AssertContains(mysqlUtcTimestampSql, "UTC_TIMESTAMP() AS synced_at", "Converted MySQL SQL should use UTC_TIMESTAMP.");
        AssertNotContains(mysqlUtcTimestampSql, "GETUTCDATE()", "Converted MySQL SQL should remove GETUTCDATE.");

        object oracleUtcTimestampPreview = BuildViewSqlPreview(
            "SELECT UTC_TIMESTAMP() AS synced_at FROM audit_log",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleUtcTimestampPreview, "CanConvert"), "MySQL UTC_TIMESTAMP should convert to Oracle.");
        string oracleUtcTimestampSql = (string)GetProperty(oracleUtcTimestampPreview, "ConvertedSql");
        AssertContains(oracleUtcTimestampSql, "SYS_EXTRACT_UTC(SYSTIMESTAMP) AS synced_at", "Converted Oracle SQL should extract UTC from SYSTIMESTAMP.");

        object pgUtcTimestampPreview = BuildViewSqlPreview(
            "SELECT GETUTCDATE() AS synced_at FROM audit_log",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgUtcTimestampPreview, "CanConvert"), "SQL Server GETUTCDATE should convert to PostgreSQL.");
        string pgUtcTimestampSql = (string)GetProperty(pgUtcTimestampPreview, "ConvertedSql");
        AssertContains(pgUtcTimestampSql, "CURRENT_TIMESTAMP AT TIME ZONE 'UTC' AS synced_at", "Converted PostgreSQL SQL should use UTC timestamp expression.");

        object mysqlSysUtcDateTimePreview = BuildViewSqlPreview(
            "SELECT SYSUTCDATETIME() AS synced_at FROM audit_log",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlSysUtcDateTimePreview, "CanConvert"), "SQL Server SYSUTCDATETIME should convert to MySQL.");
        string mysqlSysUtcDateTimeSql = (string)GetProperty(mysqlSysUtcDateTimePreview, "ConvertedSql");
        AssertContains(mysqlSysUtcDateTimeSql, "UTC_TIMESTAMP() AS synced_at", "Converted MySQL SQL should use UTC_TIMESTAMP for SYSUTCDATETIME.");
        AssertNotContains(mysqlSysUtcDateTimeSql, "SYSUTCDATETIME", "Converted MySQL SQL should remove SYSUTCDATETIME.");

        object pgSysUtcDateTimePreview = BuildViewSqlPreview(
            "SELECT SYSUTCDATETIME() AS synced_at FROM audit_log",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgSysUtcDateTimePreview, "CanConvert"), "SQL Server SYSUTCDATETIME should convert to PostgreSQL.");
        string pgSysUtcDateTimeSql = (string)GetProperty(pgSysUtcDateTimePreview, "ConvertedSql");
        AssertContains(pgSysUtcDateTimeSql, "CURRENT_TIMESTAMP AT TIME ZONE 'UTC' AS synced_at", "Converted PostgreSQL SQL should use UTC timestamp expression for SYSUTCDATETIME.");

        object sqliteSysDateTimePreview = BuildViewSqlPreview(
            "SELECT SYSDATETIME() AS checked_at FROM audit_log",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteSysDateTimePreview, "CanConvert"), "SQL Server SYSDATETIME should convert to SQLite.");
        string sqliteSysDateTimeSql = (string)GetProperty(sqliteSysDateTimePreview, "ConvertedSql");
        AssertContains(sqliteSysDateTimeSql, "CURRENT_TIMESTAMP AS checked_at", "Converted SQLite SQL should use CURRENT_TIMESTAMP for SYSDATETIME.");

        object mysqlCurrentTimestampCallPreview = BuildViewSqlPreview(
            "SELECT CURRENT_TIMESTAMP() AS checked_at FROM audit_log",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlCurrentTimestampCallPreview, "CanConvert"), "CURRENT_TIMESTAMP() call should convert to MySQL.");
        string mysqlCurrentTimestampCallSql = (string)GetProperty(mysqlCurrentTimestampCallPreview, "ConvertedSql");
        AssertContains(mysqlCurrentTimestampCallSql, "NOW() AS checked_at", "Converted MySQL SQL should normalize CURRENT_TIMESTAMP() to NOW().");
        AssertNotContains(mysqlCurrentTimestampCallSql, "CURRENT_TIMESTAMP()", "Converted MySQL SQL should remove CURRENT_TIMESTAMP() call form.");

        object mssqlCurrentTimestampCallPreview = BuildViewSqlPreview(
            "SELECT CURRENT_TIMESTAMP() AS checked_at FROM audit_log",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlCurrentTimestampCallPreview, "CanConvert"), "CURRENT_TIMESTAMP() call should convert to SQL Server.");
        string mssqlCurrentTimestampCallSql = (string)GetProperty(mssqlCurrentTimestampCallPreview, "ConvertedSql");
        AssertContains(mssqlCurrentTimestampCallSql, "GETDATE() AS checked_at", "Converted SQL Server SQL should normalize CURRENT_TIMESTAMP() to GETDATE().");

        object pgCurrentUserPreview = BuildViewSqlPreview(
            "SELECT CURRENT_USER() AS current_user_name FROM audit_log",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgCurrentUserPreview, "CanConvert"), "MySQL CURRENT_USER should convert to PostgreSQL.");
        string pgCurrentUserSql = (string)GetProperty(pgCurrentUserPreview, "ConvertedSql");
        AssertContains(pgCurrentUserSql, "CURRENT_USER AS current_user_name", "Converted PostgreSQL SQL should use CURRENT_USER.");
        AssertNotContains(pgCurrentUserSql, "CURRENT_USER()", "Converted PostgreSQL SQL should remove MySQL CURRENT_USER call form.");

        object oracleSessionUserPreview = BuildViewSqlPreview(
            "SELECT SESSION_USER AS session_user_name FROM audit_log",
            "postgresql",
            "oracle");
        Assert((bool)GetProperty(oracleSessionUserPreview, "CanConvert"), "PostgreSQL SESSION_USER should convert to Oracle.");
        string oracleSessionUserSql = (string)GetProperty(oracleSessionUserPreview, "ConvertedSql");
        AssertContains(oracleSessionUserSql, "SYS_CONTEXT('USERENV','SESSION_USER') AS session_user_name", "Converted Oracle SQL should use SYS_CONTEXT for SESSION_USER.");

        object mysqlSystemUserPreview = BuildViewSqlPreview(
            "SELECT SYSTEM_USER AS login_name FROM audit_log",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlSystemUserPreview, "CanConvert"), "SQL Server SYSTEM_USER should convert to MySQL.");
        string mysqlSystemUserSql = (string)GetProperty(mysqlSystemUserPreview, "ConvertedSql");
        AssertContains(mysqlSystemUserSql, "USER() AS login_name", "Converted MySQL SQL should use USER() for SYSTEM_USER.");

        object mssqlDatabaseNamePreview = BuildViewSqlPreview(
            "SELECT DATABASE() AS database_name FROM audit_log",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlDatabaseNamePreview, "CanConvert"), "MySQL DATABASE should convert to SQL Server.");
        string mssqlDatabaseNameSql = (string)GetProperty(mssqlDatabaseNamePreview, "ConvertedSql");
        AssertContains(mssqlDatabaseNameSql, "DB_NAME() AS database_name", "Converted SQL Server SQL should use DB_NAME().");
        AssertNotContains(mssqlDatabaseNameSql, "DATABASE()", "Converted SQL Server SQL should remove DATABASE().");

        object mysqlCurrentDatabasePreview = BuildViewSqlPreview(
            "SELECT CURRENT_DATABASE() AS database_name FROM audit_log",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlCurrentDatabasePreview, "CanConvert"), "PostgreSQL CURRENT_DATABASE should convert to MySQL.");
        string mysqlCurrentDatabaseSql = (string)GetProperty(mysqlCurrentDatabasePreview, "ConvertedSql");
        AssertContains(mysqlCurrentDatabaseSql, "DATABASE() AS database_name", "Converted MySQL SQL should use DATABASE().");
        AssertNotContains(mysqlCurrentDatabaseSql, "CURRENT_DATABASE", "Converted MySQL SQL should remove CURRENT_DATABASE.");

        object oracleDbNamePreview = BuildViewSqlPreview(
            "SELECT DB_NAME() AS database_name FROM audit_log",
            "mssql",
            "oracle");
        Assert((bool)GetProperty(oracleDbNamePreview, "CanConvert"), "SQL Server DB_NAME should convert to Oracle.");
        string oracleDbNameSql = (string)GetProperty(oracleDbNamePreview, "ConvertedSql");
        AssertContains(oracleDbNameSql, "SYS_CONTEXT('USERENV','DB_NAME') AS database_name", "Converted Oracle SQL should use SYS_CONTEXT for DB_NAME.");

        object pgSchemaPreview = BuildViewSqlPreview(
            "SELECT SCHEMA() AS schema_name FROM audit_log",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgSchemaPreview, "CanConvert"), "MySQL SCHEMA should convert to PostgreSQL.");
        string pgSchemaSql = (string)GetProperty(pgSchemaPreview, "ConvertedSql");
        AssertContains(pgSchemaSql, "CURRENT_SCHEMA AS schema_name", "Converted PostgreSQL SQL should use CURRENT_SCHEMA.");
        AssertNotContains(pgSchemaSql, "SCHEMA()", "Converted PostgreSQL SQL should remove SCHEMA().");

        object mssqlSchemaPreview = BuildViewSqlPreview(
            "SELECT CURRENT_SCHEMA() AS schema_name FROM audit_log",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlSchemaPreview, "CanConvert"), "PostgreSQL CURRENT_SCHEMA should convert to SQL Server.");
        string mssqlSchemaSql = (string)GetProperty(mssqlSchemaPreview, "ConvertedSql");
        AssertContains(mssqlSchemaSql, "SCHEMA_NAME() AS schema_name", "Converted SQL Server SQL should use SCHEMA_NAME().");
        AssertNotContains(mssqlSchemaSql, "CURRENT_SCHEMA", "Converted SQL Server SQL should remove CURRENT_SCHEMA.");

        object oracleSchemaPreview = BuildViewSqlPreview(
            "SELECT SCHEMA_NAME() AS schema_name FROM audit_log",
            "mssql",
            "oracle");
        Assert((bool)GetProperty(oracleSchemaPreview, "CanConvert"), "SQL Server SCHEMA_NAME should convert to Oracle.");
        string oracleSchemaSql = (string)GetProperty(oracleSchemaPreview, "ConvertedSql");
        AssertContains(oracleSchemaSql, "SYS_CONTEXT('USERENV','CURRENT_SCHEMA') AS schema_name", "Converted Oracle SQL should use SYS_CONTEXT for current schema.");

        object mssqlCurrentDatePreview = BuildViewSqlPreview(
            "SELECT CURDATE() AS today FROM users",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlCurrentDatePreview, "CanConvert"), "MySQL CURDATE should convert to SQL Server.");
        string mssqlCurrentDateSql = (string)GetProperty(mssqlCurrentDatePreview, "ConvertedSql");
        AssertContains(mssqlCurrentDateSql, "CAST(GETDATE() AS date)", "Converted SQL Server SQL should use a date expression.");

        object mssqlCurrentTimePreview = BuildViewSqlPreview(
            "SELECT CURTIME() AS checked_time FROM audit_log",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlCurrentTimePreview, "CanConvert"), "MySQL CURTIME should convert to SQL Server.");
        string mssqlCurrentTimeSql = (string)GetProperty(mssqlCurrentTimePreview, "ConvertedSql");
        AssertContains(mssqlCurrentTimeSql, "CAST(GETDATE() AS time)", "Converted SQL Server SQL should use a time expression.");
        AssertNotContains(mssqlCurrentTimeSql, "CURTIME", "Converted SQL Server SQL should remove CURTIME.");

        object sqliteCurrentTimePreview = BuildViewSqlPreview(
            "SELECT CURRENT_TIME AS checked_time FROM audit_log",
            "postgresql",
            "sqlite");
        Assert((bool)GetProperty(sqliteCurrentTimePreview, "CanConvert"), "PostgreSQL CURRENT_TIME should convert to SQLite.");
        string sqliteCurrentTimeSql = (string)GetProperty(sqliteCurrentTimePreview, "ConvertedSql");
        AssertContains(sqliteCurrentTimeSql, "time('now') AS checked_time", "Converted SQLite SQL should use time('now').");

        object mysqlCurrentTimePreview = BuildViewSqlPreview(
            "SELECT CURRENT_TIME AS checked_time FROM audit_log",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlCurrentTimePreview, "CanConvert"), "PostgreSQL CURRENT_TIME should convert to MySQL.");
        string mysqlCurrentTimeSql = (string)GetProperty(mysqlCurrentTimePreview, "ConvertedSql");
        AssertContains(mysqlCurrentTimeSql, "CURTIME() AS checked_time", "Converted MySQL SQL should use CURTIME().");

        object mysqlLocalTimestampPreview = BuildViewSqlPreview(
            "SELECT LOCALTIMESTAMP AS checked_at FROM audit_log",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlLocalTimestampPreview, "CanConvert"), "PostgreSQL LOCALTIMESTAMP should convert to MySQL.");
        string mysqlLocalTimestampSql = (string)GetProperty(mysqlLocalTimestampPreview, "ConvertedSql");
        AssertContains(mysqlLocalTimestampSql, "NOW() AS checked_at", "Converted MySQL SQL should use NOW for LOCALTIMESTAMP.");
        AssertNotContains(mysqlLocalTimestampSql, "LOCALTIME", "Converted MySQL SQL should not leave LOCALTIME prefix artifacts.");

        object mssqlLocalTimePreview = BuildViewSqlPreview(
            "SELECT LOCALTIME AS checked_time FROM audit_log",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlLocalTimePreview, "CanConvert"), "PostgreSQL LOCALTIME should convert to SQL Server.");
        string mssqlLocalTimeSql = (string)GetProperty(mssqlLocalTimePreview, "ConvertedSql");
        AssertContains(mssqlLocalTimeSql, "CAST(GETDATE() AS time) AS checked_time", "Converted SQL Server SQL should use a time expression for LOCALTIME.");

        object sqliteLocalTimestampPreview = BuildViewSqlPreview(
            "SELECT LOCALTIMESTAMP AS checked_at FROM audit_log",
            "postgresql",
            "sqlite");
        Assert((bool)GetProperty(sqliteLocalTimestampPreview, "CanConvert"), "PostgreSQL LOCALTIMESTAMP should convert to SQLite.");
        string sqliteLocalTimestampSql = (string)GetProperty(sqliteLocalTimestampPreview, "ConvertedSql");
        AssertContains(sqliteLocalTimestampSql, "CURRENT_TIMESTAMP AS checked_at", "Converted SQLite SQL should use CURRENT_TIMESTAMP for LOCALTIMESTAMP.");

        object mssqlSysdatePreview = BuildViewSqlPreview(
            "SELECT SYSDATE AS checked_at, 'SYSDATE' AS literal_value FROM dual",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlSysdatePreview, "CanConvert"), "Oracle SYSDATE should convert to SQL Server.");
        string mssqlSysdateSql = (string)GetProperty(mssqlSysdatePreview, "ConvertedSql");
        AssertContains(mssqlSysdateSql, "GETDATE() AS checked_at", "Converted SQL Server SQL should use GETDATE for SYSDATE.");
        AssertContains(mssqlSysdateSql, "'SYSDATE' AS literal_value", "Converted SQL Server SQL should preserve SYSDATE inside string literals.");

        object mysqlSystimestampPreview = BuildViewSqlPreview(
            "SELECT SYSTIMESTAMP AS checked_at FROM audit_log",
            "oracle",
            "mysql");
        Assert((bool)GetProperty(mysqlSystimestampPreview, "CanConvert"), "Oracle SYSTIMESTAMP should convert to MySQL.");
        string mysqlSystimestampSql = (string)GetProperty(mysqlSystimestampPreview, "ConvertedSql");
        AssertContains(mysqlSystimestampSql, "NOW() AS checked_at", "Converted MySQL SQL should use NOW for SYSTIMESTAMP.");

        object sqliteSysdatePreview = BuildViewSqlPreview(
            "SELECT SYSDATE AS checked_at FROM audit_log",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteSysdatePreview, "CanConvert"), "Oracle SYSDATE should convert to SQLite.");
        string sqliteSysdateSql = (string)GetProperty(sqliteSysdatePreview, "ConvertedSql");
        AssertContains(sqliteSysdateSql, "CURRENT_TIMESTAMP AS checked_at", "Converted SQLite SQL should use CURRENT_TIMESTAMP for SYSDATE.");

        object oracleSysdatePreview = BuildViewSqlPreview(
            "SELECT SYSDATE AS checked_at FROM dual",
            "oracle",
            "oracle");
        Assert((bool)GetProperty(oracleSysdatePreview, "CanConvert"), "Oracle target should keep SYSDATE.");
        string oracleSysdateSql = (string)GetProperty(oracleSysdatePreview, "ConvertedSql");
        AssertContains(oracleSysdateSql, "SYSDATE AS checked_at", "Converted Oracle SQL should keep SYSDATE.");

        object mysqlFormatPreview = BuildViewSqlPreview(
            "SELECT FORMAT(created_at, 'yyyy-MM-dd HH:mm:ss') AS created_text FROM orders",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlFormatPreview, "CanConvert"), "SQL Server FORMAT should convert to MySQL.");
        string mysqlFormatSql = (string)GetProperty(mysqlFormatPreview, "ConvertedSql");
        AssertContains(mysqlFormatSql, "DATE_FORMAT(created_at, '%Y-%m-%d %H:%i:%s')", "Converted MySQL SQL should use DATE_FORMAT.");

        object sqliteToCharPreview = BuildViewSqlPreview(
            "SELECT TO_CHAR(created_at, 'YYYY-MM-DD') AS created_text FROM orders",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteToCharPreview, "CanConvert"), "Oracle TO_CHAR should convert to SQLite.");
        string sqliteToCharSql = (string)GetProperty(sqliteToCharPreview, "ConvertedSql");
        AssertContains(sqliteToCharSql, "strftime('%Y-%m-%d', created_at)", "Converted SQLite SQL should use strftime.");

        object mysqlConvertDatePreview = BuildViewSqlPreview(
            "SELECT CONVERT(varchar(10), created_at, 23) AS created_text FROM orders",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlConvertDatePreview, "CanConvert"), "SQL Server CONVERT style 23 should convert to MySQL.");
        string mysqlConvertDateSql = (string)GetProperty(mysqlConvertDatePreview, "ConvertedSql");
        AssertContains(mysqlConvertDateSql, "DATE_FORMAT(created_at, '%Y-%m-%d')", "Converted MySQL SQL should use DATE_FORMAT for CONVERT style 23.");

        object mysqlConvertDateLiteralPreview = BuildViewSqlPreview(
            "SELECT CONVERT(varchar(10), created_at, 23) AS created_text, 'CONVERT(varchar(10), created_at, 23)' AS literal_note FROM orders",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlConvertDateLiteralPreview, "CanConvert"), "SQL Server CONVERT date style should convert while preserving literals.");
        string mysqlConvertDateLiteralSql = (string)GetProperty(mysqlConvertDateLiteralPreview, "ConvertedSql");
        AssertContains(mysqlConvertDateLiteralSql, "DATE_FORMAT(created_at, '%Y-%m-%d') AS created_text", "Converted MySQL SQL should convert real CONVERT date style.");
        AssertContains(mysqlConvertDateLiteralSql, "'CONVERT(varchar(10), created_at, 23)' AS literal_note", "Converted MySQL SQL should preserve CONVERT date style text inside string literals.");

        object oracleConvertDateTimePreview = BuildViewSqlPreview(
            "SELECT CONVERT(varchar(19), created_at, 120) AS created_text FROM orders",
            "mssql",
            "oracle");
        Assert((bool)GetProperty(oracleConvertDateTimePreview, "CanConvert"), "SQL Server CONVERT style 120 should convert to Oracle.");
        string oracleConvertDateTimeSql = (string)GetProperty(oracleConvertDateTimePreview, "ConvertedSql");
        AssertContains(oracleConvertDateTimeSql, "TO_CHAR(created_at, 'YYYY-MM-DD HH24:MI:SS')", "Converted Oracle SQL should use TO_CHAR for CONVERT style 120.");

        object sqliteConvertDatePreview = BuildViewSqlPreview(
            "SELECT CONVERT(char(10), created_at, 23) AS created_text FROM orders",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteConvertDatePreview, "CanConvert"), "SQL Server CONVERT style 23 should convert to SQLite.");
        string sqliteConvertDateSql = (string)GetProperty(sqliteConvertDatePreview, "ConvertedSql");
        AssertContains(sqliteConvertDateSql, "strftime('%Y-%m-%d', created_at)", "Converted SQLite SQL should use strftime for CONVERT style 23.");

        object mssqlToDatePreview = BuildViewSqlPreview(
            "SELECT TO_DATE(order_date_text, 'YYYY-MM-DD') AS order_date FROM orders",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlToDatePreview, "CanConvert"), "Oracle TO_DATE should convert to SQL Server.");
        string mssqlToDateSql = (string)GetProperty(mssqlToDatePreview, "ConvertedSql");
        AssertContains(mssqlToDateSql, "CONVERT(date, order_date_text, 23)", "Converted SQL Server SQL should use CONVERT date style 23.");

        object sqliteToDatePreview = BuildViewSqlPreview(
            "SELECT TO_DATE(order_date_text, 'YYYY-MM-DD') AS order_date FROM orders",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteToDatePreview, "CanConvert"), "Oracle TO_DATE should convert to SQLite.");
        string sqliteToDateSql = (string)GetProperty(sqliteToDatePreview, "ConvertedSql");
        AssertContains(sqliteToDateSql, "date(order_date_text)", "Converted SQLite SQL should use date().");

        object oracleStrToDatePreview = BuildViewSqlPreview(
            "SELECT STR_TO_DATE(created_text, '%Y-%m-%d %H:%i:%s') AS created_at FROM orders",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleStrToDatePreview, "CanConvert"), "MySQL STR_TO_DATE should convert to Oracle.");
        string oracleStrToDateSql = (string)GetProperty(oracleStrToDatePreview, "ConvertedSql");
        AssertContains(oracleStrToDateSql, "TO_DATE(created_text, 'YYYY-MM-DD HH24:MI:SS')", "Converted Oracle SQL should use TO_DATE.");

        object pgStrToDatePreview = BuildViewSqlPreview(
            "SELECT STR_TO_DATE(created_text, '%Y-%m-%d %H:%i:%s') AS created_at FROM orders",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgStrToDatePreview, "CanConvert"), "MySQL STR_TO_DATE should convert to PostgreSQL.");
        string pgStrToDateSql = (string)GetProperty(pgStrToDatePreview, "ConvertedSql");
        AssertContains(pgStrToDateSql, "TO_TIMESTAMP(created_text, 'YYYY-MM-DD HH24:MI:SS')", "Converted PostgreSQL SQL should use TO_TIMESTAMP.");

        object mysqlStrToDatePreview = BuildViewSqlPreview(
            "SELECT STR_TO_DATE(created_text, '%Y-%m-%d') AS created_at FROM orders",
            "mysql",
            "mysql");
        Assert((bool)GetProperty(mysqlStrToDatePreview, "CanConvert"), "MySQL target should keep STR_TO_DATE.");
        string mysqlStrToDateSql = (string)GetProperty(mysqlStrToDatePreview, "ConvertedSql");
        AssertContains(mysqlStrToDateSql, "STR_TO_DATE(created_text, '%Y-%m-%d')", "Converted MySQL SQL should keep STR_TO_DATE.");

        object mssqlToTimestampPreview = BuildViewSqlPreview(
            "SELECT TO_TIMESTAMP(created_text, 'YYYY-MM-DD HH24:MI:SS') AS created_at FROM orders",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlToTimestampPreview, "CanConvert"), "Oracle TO_TIMESTAMP should convert to SQL Server.");
        string mssqlToTimestampSql = (string)GetProperty(mssqlToTimestampPreview, "ConvertedSql");
        AssertContains(mssqlToTimestampSql, "CONVERT(datetime, created_text, 120)", "Converted SQL Server SQL should use CONVERT datetime style 120.");

        object mysqlToTimestampPreview = BuildViewSqlPreview(
            "SELECT TO_TIMESTAMP(created_text, 'YYYY-MM-DD HH24:MI:SS') AS created_at FROM orders",
            "oracle",
            "mysql");
        Assert((bool)GetProperty(mysqlToTimestampPreview, "CanConvert"), "Oracle TO_TIMESTAMP should convert to MySQL.");
        string mysqlToTimestampSql = (string)GetProperty(mysqlToTimestampPreview, "ConvertedSql");
        AssertContains(mysqlToTimestampSql, "STR_TO_DATE(created_text, '%Y-%m-%d %H:%i:%s')", "Converted MySQL SQL should use STR_TO_DATE for timestamp parsing.");

        object sqliteToTimestampPreview = BuildViewSqlPreview(
            "SELECT TO_TIMESTAMP(created_text, 'YYYY-MM-DD') AS created_at FROM orders",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteToTimestampPreview, "CanConvert"), "Oracle TO_TIMESTAMP should convert to SQLite.");
        string sqliteToTimestampSql = (string)GetProperty(sqliteToTimestampPreview, "ConvertedSql");
        AssertContains(sqliteToTimestampSql, "datetime(created_text)", "Converted SQLite SQL should use datetime() for TO_TIMESTAMP even with date-only pattern.");

        object pgToTimestampPreview = BuildViewSqlPreview(
            "SELECT TO_TIMESTAMP(created_text, 'YYYY-MM-DD HH24:MI:SS') AS created_at FROM orders",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgToTimestampPreview, "CanConvert"), "PostgreSQL target should keep TO_TIMESTAMP.");
        string pgToTimestampSql = (string)GetProperty(pgToTimestampPreview, "ConvertedSql");
        AssertContains(pgToTimestampSql, "TO_TIMESTAMP(created_text, 'YYYY-MM-DD HH24:MI:SS')", "Converted PostgreSQL SQL should keep TO_TIMESTAMP.");

        object mssqlDateOnlyPreview = BuildViewSqlPreview(
            "SELECT DATE(created_at) AS created_date FROM orders",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlDateOnlyPreview, "CanConvert"), "MySQL DATE should convert to SQL Server.");
        string mssqlDateOnlySql = (string)GetProperty(mssqlDateOnlyPreview, "ConvertedSql");
        AssertContains(mssqlDateOnlySql, "CAST(created_at AS date)", "Converted SQL Server SQL should cast to date.");

        object pgOracleTruncPreview = BuildViewSqlPreview(
            "SELECT TRUNC(created_at) AS created_date FROM orders",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgOracleTruncPreview, "CanConvert"), "Oracle TRUNC date should convert to PostgreSQL.");
        string pgOracleTruncSql = (string)GetProperty(pgOracleTruncPreview, "ConvertedSql");
        AssertContains(pgOracleTruncSql, "CAST(created_at AS date)", "Converted PostgreSQL SQL should cast to date.");

        object oracleDateOnlyPreview = BuildViewSqlPreview(
            "SELECT DATE(created_at) AS created_date FROM orders",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleDateOnlyPreview, "CanConvert"), "MySQL DATE should convert to Oracle.");
        string oracleDateOnlySql = (string)GetProperty(oracleDateOnlyPreview, "ConvertedSql");
        AssertContains(oracleDateOnlySql, "TRUNC(created_at)", "Converted Oracle SQL should use TRUNC for date-only value.");

        object mssqlDateTruncMonthPreview = BuildViewSqlPreview(
            "SELECT DATE_TRUNC('month', created_at) AS month_start FROM orders",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlDateTruncMonthPreview, "CanConvert"), "PostgreSQL DATE_TRUNC month should convert to SQL Server.");
        string mssqlDateTruncMonthSql = (string)GetProperty(mssqlDateTruncMonthPreview, "ConvertedSql");
        AssertContains(mssqlDateTruncMonthSql, "DATEFROMPARTS(YEAR(created_at), MONTH(created_at), 1)", "Converted SQL Server SQL should build month start.");

        object oracleDateTruncHourPreview = BuildViewSqlPreview(
            "SELECT DATE_TRUNC('hour', created_at) AS hour_start FROM orders",
            "postgresql",
            "oracle");
        Assert((bool)GetProperty(oracleDateTruncHourPreview, "CanConvert"), "PostgreSQL DATE_TRUNC hour should convert to Oracle.");
        string oracleDateTruncHourSql = (string)GetProperty(oracleDateTruncHourPreview, "ConvertedSql");
        AssertContains(oracleDateTruncHourSql, "TRUNC(created_at, 'HH24')", "Converted Oracle SQL should truncate to the hour.");

        object mssqlDateTruncMinutePreview = BuildViewSqlPreview(
            "SELECT DATE_TRUNC('minute', created_at) AS minute_start FROM orders",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlDateTruncMinutePreview, "CanConvert"), "PostgreSQL DATE_TRUNC minute should convert to SQL Server.");
        string mssqlDateTruncMinuteSql = (string)GetProperty(mssqlDateTruncMinutePreview, "ConvertedSql");
        AssertContains(mssqlDateTruncMinuteSql, "DATEADD(minute, DATEDIFF(minute, 0, created_at), 0)", "Converted SQL Server SQL should truncate to the minute.");

        object mysqlDateTruncSecondPreview = BuildViewSqlPreview(
            "SELECT DATE_TRUNC('second', created_at) AS second_start FROM orders",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlDateTruncSecondPreview, "CanConvert"), "PostgreSQL DATE_TRUNC second should convert to MySQL.");
        string mysqlDateTruncSecondSql = (string)GetProperty(mysqlDateTruncSecondPreview, "ConvertedSql");
        AssertContains(mysqlDateTruncSecondSql, "STR_TO_DATE(DATE_FORMAT(created_at, '%Y-%m-%d %H:%i:%s'), '%Y-%m-%d %H:%i:%s')", "Converted MySQL SQL should truncate to second precision.");

        object sqliteOracleTruncMinutePreview = BuildViewSqlPreview(
            "SELECT TRUNC(created_at, 'MI') AS minute_start FROM orders",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteOracleTruncMinutePreview, "CanConvert"), "Oracle TRUNC minute should convert to SQLite.");
        string sqliteOracleTruncMinuteSql = (string)GetProperty(sqliteOracleTruncMinutePreview, "ConvertedSql");
        AssertContains(sqliteOracleTruncMinuteSql, "strftime('%Y-%m-%d %H:%M:00', created_at)", "Converted SQLite SQL should truncate to the minute.");

        object mysqlOracleTruncMonthPreview = BuildViewSqlPreview(
            "SELECT TRUNC(created_at, 'MM') AS month_start FROM orders",
            "oracle",
            "mysql");
        Assert((bool)GetProperty(mysqlOracleTruncMonthPreview, "CanConvert"), "Oracle TRUNC month should convert to MySQL.");
        string mysqlOracleTruncMonthSql = (string)GetProperty(mysqlOracleTruncMonthPreview, "ConvertedSql");
        AssertContains(mysqlOracleTruncMonthSql, "STR_TO_DATE(DATE_FORMAT(created_at, '%Y-%m-01'), '%Y-%m-%d')", "Converted MySQL SQL should build month start.");

        object sqliteDateTruncYearPreview = BuildViewSqlPreview(
            "SELECT DATE_TRUNC('year', created_at) AS year_start FROM orders",
            "postgresql",
            "sqlite");
        Assert((bool)GetProperty(sqliteDateTruncYearPreview, "CanConvert"), "PostgreSQL DATE_TRUNC year should convert to SQLite.");
        string sqliteDateTruncYearSql = (string)GetProperty(sqliteDateTruncYearPreview, "ConvertedSql");
        AssertContains(sqliteDateTruncYearSql, "strftime('%Y-01-01', created_at)", "Converted SQLite SQL should build year start.");

        object mysqlCurrentDatePreview = BuildViewSqlPreview(
            "SELECT CURRENT_DATE AS today FROM users",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlCurrentDatePreview, "CanConvert"), "PostgreSQL CURRENT_DATE should convert to MySQL.");
        string mysqlCurrentDateSql = (string)GetProperty(mysqlCurrentDatePreview, "ConvertedSql");
        AssertContains(mysqlCurrentDateSql, "CURDATE()", "Converted MySQL SQL should use CURDATE().");

        object mysqlEndOfMonthPreview = BuildViewSqlPreview(
            "SELECT EOMONTH(created_at) AS month_end FROM orders",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlEndOfMonthPreview, "CanConvert"), "SQL Server EOMONTH should convert to MySQL.");
        string mysqlEndOfMonthSql = (string)GetProperty(mysqlEndOfMonthPreview, "ConvertedSql");
        AssertContains(mysqlEndOfMonthSql, "LAST_DAY(created_at)", "Converted MySQL SQL should use LAST_DAY.");

        object pgEndOfMonthOffsetPreview = BuildViewSqlPreview(
            "SELECT EOMONTH(created_at, billing_offset) AS billing_month_end FROM invoices",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgEndOfMonthOffsetPreview, "CanConvert"), "SQL Server EOMONTH with offset should convert to PostgreSQL.");
        string pgEndOfMonthOffsetSql = (string)GetProperty(pgEndOfMonthOffsetPreview, "ConvertedSql");
        AssertContains(pgEndOfMonthOffsetSql, "(DATE_TRUNC('month', (created_at + (billing_offset * INTERVAL '1 month'))) + INTERVAL '1 month - 1 day')::date", "Converted PostgreSQL SQL should build month end with offset.");
        AssertNotContains(pgEndOfMonthOffsetSql, "EOMONTH", "Converted PostgreSQL SQL should remove EOMONTH.");

        object pgEndOfMonthLiteralPreview = BuildViewSqlPreview(
            "SELECT EOMONTH(created_at, billing_offset) AS billing_month_end, 'EOMONTH(created_at, billing_offset)' AS literal_note FROM invoices",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgEndOfMonthLiteralPreview, "CanConvert"), "SQL Server EOMONTH should convert while preserving literals.");
        string pgEndOfMonthLiteralSql = (string)GetProperty(pgEndOfMonthLiteralPreview, "ConvertedSql");
        AssertContains(pgEndOfMonthLiteralSql, "(DATE_TRUNC('month', (created_at + (billing_offset * INTERVAL '1 month'))) + INTERVAL '1 month - 1 day')::date AS billing_month_end", "Converted PostgreSQL SQL should convert EOMONTH with offset.");
        AssertContains(pgEndOfMonthLiteralSql, "'EOMONTH(created_at, billing_offset)' AS literal_note", "Converted PostgreSQL SQL should preserve EOMONTH text inside string literals.");

        object sqliteEndOfMonthOffsetPreview = BuildViewSqlPreview(
            "SELECT EOMONTH(created_at, -1) AS previous_month_end FROM invoices",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteEndOfMonthOffsetPreview, "CanConvert"), "SQL Server EOMONTH with offset should convert to SQLite.");
        string sqliteEndOfMonthOffsetSql = (string)GetProperty(sqliteEndOfMonthOffsetPreview, "ConvertedSql");
        AssertContains(sqliteEndOfMonthOffsetSql, "date(created_at, printf('%+d month', -1), 'start of month', '+1 month', '-1 day')", "Converted SQLite SQL should use date modifiers with offset.");

        object oracleEndOfMonthOffsetPreview = BuildViewSqlPreview(
            "SELECT EOMONTH(created_at, 2) AS future_month_end FROM invoices",
            "mssql",
            "oracle");
        Assert((bool)GetProperty(oracleEndOfMonthOffsetPreview, "CanConvert"), "SQL Server EOMONTH with offset should convert to Oracle.");
        string oracleEndOfMonthOffsetSql = (string)GetProperty(oracleEndOfMonthOffsetPreview, "ConvertedSql");
        AssertContains(oracleEndOfMonthOffsetSql, "LAST_DAY(ADD_MONTHS(created_at, 2))", "Converted Oracle SQL should use LAST_DAY with ADD_MONTHS.");

        object mssqlLastDayPreview = BuildViewSqlPreview(
            "SELECT LAST_DAY(created_at) AS month_end FROM orders",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlLastDayPreview, "CanConvert"), "MySQL LAST_DAY should convert to SQL Server.");
        string mssqlLastDaySql = (string)GetProperty(mssqlLastDayPreview, "ConvertedSql");
        AssertContains(mssqlLastDaySql, "EOMONTH(created_at)", "Converted SQL Server SQL should use EOMONTH.");

        object pgLastDayPreview = BuildViewSqlPreview(
            "SELECT LAST_DAY(created_at) AS month_end FROM orders",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgLastDayPreview, "CanConvert"), "Oracle LAST_DAY should convert to PostgreSQL.");
        string pgLastDaySql = (string)GetProperty(pgLastDayPreview, "ConvertedSql");
        AssertContains(pgLastDaySql, "(DATE_TRUNC('month', created_at) + INTERVAL '1 month - 1 day')::date", "Converted PostgreSQL SQL should build month end.");
        AssertNotContains(pgLastDaySql, "LAST_DAY", "Converted PostgreSQL SQL should remove LAST_DAY.");

        object pgLastDayLiteralPreview = BuildViewSqlPreview(
            "SELECT LAST_DAY(DATE_ADD(created_at, INTERVAL 1 MONTH)) AS month_end, 'LAST_DAY(DATE_ADD(created_at, INTERVAL 1 MONTH))' AS literal_note FROM orders",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgLastDayLiteralPreview, "CanConvert"), "MySQL LAST_DAY should convert while preserving literals.");
        string pgLastDayLiteralSql = (string)GetProperty(pgLastDayLiteralPreview, "ConvertedSql");
        AssertContains(pgLastDayLiteralSql, "(DATE_TRUNC('month', created_at + INTERVAL '1 month') + INTERVAL '1 month - 1 day')::date AS month_end", "Converted PostgreSQL SQL should convert nested LAST_DAY expression.");
        AssertContains(pgLastDayLiteralSql, "'LAST_DAY(DATE_ADD(created_at, INTERVAL 1 MONTH))' AS literal_note", "Converted PostgreSQL SQL should preserve LAST_DAY text inside string literals.");

        object sqliteLastDayPreview = BuildViewSqlPreview(
            "SELECT LAST_DAY(created_at) AS month_end FROM orders",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteLastDayPreview, "CanConvert"), "MySQL LAST_DAY should convert to SQLite.");
        string sqliteLastDaySql = (string)GetProperty(sqliteLastDayPreview, "ConvertedSql");
        AssertContains(sqliteLastDaySql, "date(created_at, 'start of month', '+1 month', '-1 day')", "Converted SQLite SQL should use date modifiers for month end.");

        object pgDateFromPartsPreview = BuildViewSqlPreview(
            "SELECT DATEFROMPARTS(order_year, order_month, 1) AS month_start FROM orders",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgDateFromPartsPreview, "CanConvert"), "SQL Server DATEFROMPARTS should convert to PostgreSQL.");
        string pgDateFromPartsSql = (string)GetProperty(pgDateFromPartsPreview, "ConvertedSql");
        AssertContains(pgDateFromPartsSql, "MAKE_DATE(order_year, order_month, 1)", "Converted PostgreSQL SQL should use MAKE_DATE.");
        AssertNotContains(pgDateFromPartsSql, "DATEFROMPARTS", "Converted PostgreSQL SQL should remove DATEFROMPARTS.");

        object pgNestedDateFromPartsPreview = BuildViewSqlPreview(
            "SELECT DATEFROMPARTS(ABS(order_year), order_month, order_day) AS order_date, 'DATEFROMPARTS(ABS(order_year), order_month, order_day)' AS literal_note FROM orders",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgNestedDateFromPartsPreview, "CanConvert"), "Nested SQL Server DATEFROMPARTS should convert while preserving literals.");
        string pgNestedDateFromPartsSql = (string)GetProperty(pgNestedDateFromPartsPreview, "ConvertedSql");
        AssertContains(pgNestedDateFromPartsSql, "MAKE_DATE(ABS(order_year), order_month, order_day) AS order_date", "Converted PostgreSQL SQL should convert nested DATEFROMPARTS arguments.");
        AssertContains(pgNestedDateFromPartsSql, "'DATEFROMPARTS(ABS(order_year), order_month, order_day)' AS literal_note", "Converted PostgreSQL SQL should preserve DATEFROMPARTS text inside string literals.");

        object mysqlDateFromPartsPreview = BuildViewSqlPreview(
            "SELECT DATEFROMPARTS(order_year, order_month, order_day) AS order_date FROM orders",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlDateFromPartsPreview, "CanConvert"), "SQL Server DATEFROMPARTS should convert to MySQL.");
        string mysqlDateFromPartsSql = (string)GetProperty(mysqlDateFromPartsPreview, "ConvertedSql");
        AssertContains(mysqlDateFromPartsSql, "STR_TO_DATE(CONCAT(order_year, '-', order_month, '-', order_day), '%Y-%m-%d')", "Converted MySQL SQL should build a date from parts.");

        object sqliteDateFromPartsPreview = BuildViewSqlPreview(
            "SELECT DATEFROMPARTS(order_year, order_month, order_day) AS order_date FROM orders",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteDateFromPartsPreview, "CanConvert"), "SQL Server DATEFROMPARTS should convert to SQLite.");
        string sqliteDateFromPartsSql = (string)GetProperty(sqliteDateFromPartsPreview, "ConvertedSql");
        AssertContains(sqliteDateFromPartsSql, "printf('%04d-%02d-%02d', order_year, order_month, order_day)", "Converted SQLite SQL should build an ISO date string.");

        object oracleDateFromPartsPreview = BuildViewSqlPreview(
            "SELECT DATEFROMPARTS(order_year, order_month, order_day) AS order_date FROM orders",
            "mssql",
            "oracle");
        Assert((bool)GetProperty(oracleDateFromPartsPreview, "CanConvert"), "SQL Server DATEFROMPARTS should convert to Oracle.");
        string oracleDateFromPartsSql = (string)GetProperty(oracleDateFromPartsPreview, "ConvertedSql");
        AssertContains(oracleDateFromPartsSql, "TO_DATE(order_year || '-' || order_month || '-' || order_day, 'YYYY-MM-DD')", "Converted Oracle SQL should build a date from concatenated parts.");

        object mssqlDateDiffPreview = BuildViewSqlPreview(
            "SELECT DATEDIFF(end_date, start_date) AS days_open FROM tickets",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlDateDiffPreview, "CanConvert"), "MySQL DATEDIFF should convert to SQL Server.");
        string mssqlDateDiffSql = (string)GetProperty(mssqlDateDiffPreview, "ConvertedSql");
        AssertContains(mssqlDateDiffSql, "DATEDIFF(day, start_date, end_date)", "Converted SQL Server SQL should use day datepart.");

        object mysqlDateDiffPreview = BuildViewSqlPreview(
            "SELECT DATEDIFF(day, start_date, end_date) AS days_open FROM tickets",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlDateDiffPreview, "CanConvert"), "SQL Server DATEDIFF should convert to MySQL.");
        string mysqlDateDiffSql = (string)GetProperty(mysqlDateDiffPreview, "ConvertedSql");
        AssertContains(mysqlDateDiffSql, "DATEDIFF(end_date, start_date)", "Converted MySQL SQL should use two-argument DATEDIFF.");

        object sqliteDateDiffPreview = BuildViewSqlPreview(
            "SELECT DATEDIFF(day, start_date, end_date) AS days_open FROM tickets",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteDateDiffPreview, "CanConvert"), "SQL Server DATEDIFF should convert to SQLite.");
        string sqliteDateDiffSql = (string)GetProperty(sqliteDateDiffPreview, "ConvertedSql");
        AssertContains(sqliteDateDiffSql, "CAST(julianday(end_date) - julianday(start_date) AS INTEGER)", "Converted SQLite SQL should use julianday day difference.");

        object pgTimestampDiffPreview = BuildViewSqlPreview(
            "SELECT TIMESTAMPDIFF(HOUR, start_time, end_time) AS hours_open FROM tickets",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgTimestampDiffPreview, "CanConvert"), "MySQL TIMESTAMPDIFF hour should convert to PostgreSQL.");
        string pgTimestampDiffSql = (string)GetProperty(pgTimestampDiffPreview, "ConvertedSql");
        AssertContains(pgTimestampDiffSql, "CAST(EXTRACT(EPOCH FROM (end_time - start_time)) / 3600 AS INTEGER)", "Converted PostgreSQL SQL should use EXTRACT(EPOCH) for hour difference.");
        AssertNotContains(pgTimestampDiffSql, "TIMESTAMPDIFF", "Converted PostgreSQL SQL should remove TIMESTAMPDIFF.");

        object sqliteTimestampDiffPreview = BuildViewSqlPreview(
            "SELECT TIMESTAMPDIFF(MINUTE, start_time, end_time) AS minutes_open FROM tickets",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteTimestampDiffPreview, "CanConvert"), "MySQL TIMESTAMPDIFF minute should convert to SQLite.");
        string sqliteTimestampDiffSql = (string)GetProperty(sqliteTimestampDiffPreview, "ConvertedSql");
        AssertContains(sqliteTimestampDiffSql, "CAST(((julianday(end_time) - julianday(start_time)) * 86400) / 60 AS INTEGER)", "Converted SQLite SQL should use julianday seconds for minute difference.");

        object mysqlDateDiffHourPreview = BuildViewSqlPreview(
            "SELECT DATEDIFF(hour, start_time, end_time) AS hours_open FROM tickets",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlDateDiffHourPreview, "CanConvert"), "SQL Server DATEDIFF hour should convert to MySQL.");
        string mysqlDateDiffHourSql = (string)GetProperty(mysqlDateDiffHourPreview, "ConvertedSql");
        AssertContains(mysqlDateDiffHourSql, "TIMESTAMPDIFF(HOUR, start_time, end_time)", "Converted MySQL SQL should use TIMESTAMPDIFF for hour difference.");

        object pgTimestampDiffMonthPreview = BuildViewSqlPreview(
            "SELECT TIMESTAMPDIFF(MONTH, start_date, end_date) AS months_open FROM tickets",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgTimestampDiffMonthPreview, "CanConvert"), "MySQL TIMESTAMPDIFF month should convert to PostgreSQL.");
        string pgTimestampDiffMonthSql = (string)GetProperty(pgTimestampDiffMonthPreview, "ConvertedSql");
        AssertContains(pgTimestampDiffMonthSql, "CAST((EXTRACT(YEAR FROM AGE(end_date, start_date)) * 12) + EXTRACT(MONTH FROM AGE(end_date, start_date)) AS INTEGER)", "Converted PostgreSQL SQL should use AGE for month difference.");
        AssertNotContains(pgTimestampDiffMonthSql, "TIMESTAMPDIFF", "Converted PostgreSQL SQL should remove TIMESTAMPDIFF month.");

        object pgTimestampDiffQuarterPreview = BuildViewSqlPreview(
            "SELECT TIMESTAMPDIFF(QUARTER, start_date, end_date) AS quarters_open FROM tickets",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgTimestampDiffQuarterPreview, "CanConvert"), "MySQL TIMESTAMPDIFF quarter should convert to PostgreSQL.");
        string pgTimestampDiffQuarterSql = (string)GetProperty(pgTimestampDiffQuarterPreview, "ConvertedSql");
        AssertContains(pgTimestampDiffQuarterSql, "CAST(((EXTRACT(YEAR FROM AGE(end_date, start_date)) * 12) + EXTRACT(MONTH FROM AGE(end_date, start_date))) / 3 AS INTEGER)", "Converted PostgreSQL SQL should calculate quarter difference from AGE months.");

        object sqliteTimestampDiffWeekPreview = BuildViewSqlPreview(
            "SELECT TIMESTAMPDIFF(WEEK, start_date, end_date) AS weeks_open FROM tickets",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteTimestampDiffWeekPreview, "CanConvert"), "MySQL TIMESTAMPDIFF week should convert to SQLite.");
        string sqliteTimestampDiffWeekSql = (string)GetProperty(sqliteTimestampDiffWeekPreview, "ConvertedSql");
        AssertContains(sqliteTimestampDiffWeekSql, "CAST(((julianday(end_date) - julianday(start_date)) * 86400) / 604800 AS INTEGER)", "Converted SQLite SQL should calculate week difference from julianday.");

        object sqliteDateDiffYearPreview = BuildViewSqlPreview(
            "SELECT DATEDIFF(year, hired_at, ended_at) AS years_worked FROM employees",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteDateDiffYearPreview, "CanConvert"), "SQL Server DATEDIFF year should convert to SQLite.");
        string sqliteDateDiffYearSql = (string)GetProperty(sqliteDateDiffYearPreview, "ConvertedSql");
        AssertContains(sqliteDateDiffYearSql, "(CAST(strftime('%Y', ended_at) AS INTEGER) - CAST(strftime('%Y', hired_at) AS INTEGER))", "Converted SQLite SQL should calculate year difference from strftime.");

        object oracleTimestampDiffMonthPreview = BuildViewSqlPreview(
            "SELECT TIMESTAMPDIFF(MONTH, start_date, end_date) AS months_open FROM tickets",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleTimestampDiffMonthPreview, "CanConvert"), "MySQL TIMESTAMPDIFF month should convert to Oracle.");
        string oracleTimestampDiffMonthSql = (string)GetProperty(oracleTimestampDiffMonthPreview, "ConvertedSql");
        AssertContains(oracleTimestampDiffMonthSql, "FLOOR(MONTHS_BETWEEN(end_date, start_date))", "Converted Oracle SQL should use MONTHS_BETWEEN for month difference.");

        object oracleTimestampDiffYearPreview = BuildViewSqlPreview(
            "SELECT TIMESTAMPDIFF(YEAR, hired_at, ended_at) AS years_worked FROM employees",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleTimestampDiffYearPreview, "CanConvert"), "MySQL TIMESTAMPDIFF year should convert to Oracle.");
        string oracleTimestampDiffYearSql = (string)GetProperty(oracleTimestampDiffYearPreview, "ConvertedSql");
        AssertContains(oracleTimestampDiffYearSql, "FLOOR(MONTHS_BETWEEN(ended_at, hired_at) / 12)", "Converted Oracle SQL should divide MONTHS_BETWEEN by 12 for year difference.");

        object oracleDateDiffWeekPreview = BuildViewSqlPreview(
            "SELECT DATEDIFF(week, start_date, end_date) AS weeks_open FROM tickets",
            "mssql",
            "oracle");
        Assert((bool)GetProperty(oracleDateDiffWeekPreview, "CanConvert"), "SQL Server DATEDIFF week should convert to Oracle.");
        string oracleDateDiffWeekSql = (string)GetProperty(oracleDateDiffWeekPreview, "ConvertedSql");
        AssertContains(oracleDateDiffWeekSql, "FLOOR(((end_date) - (start_date)) / 7)", "Converted Oracle SQL should calculate week difference from day delta.");

        object mssqlMonthsBetweenPreview = BuildViewSqlPreview(
            "SELECT MONTHS_BETWEEN(end_date, start_date) AS months_open FROM tickets",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlMonthsBetweenPreview, "CanConvert"), "Oracle MONTHS_BETWEEN should convert to SQL Server.");
        string mssqlMonthsBetweenSql = (string)GetProperty(mssqlMonthsBetweenPreview, "ConvertedSql");
        AssertContains(mssqlMonthsBetweenSql, "DATEDIFF(month, start_date, end_date)", "Converted SQL Server SQL should use DATEDIFF month.");

        object mysqlMonthsBetweenPreview = BuildViewSqlPreview(
            "SELECT MONTHS_BETWEEN(end_date, start_date) AS months_open FROM tickets",
            "oracle",
            "mysql");
        Assert((bool)GetProperty(mysqlMonthsBetweenPreview, "CanConvert"), "Oracle MONTHS_BETWEEN should convert to MySQL.");
        string mysqlMonthsBetweenSql = (string)GetProperty(mysqlMonthsBetweenPreview, "ConvertedSql");
        AssertContains(mysqlMonthsBetweenSql, "TIMESTAMPDIFF(MONTH, start_date, end_date)", "Converted MySQL SQL should use TIMESTAMPDIFF month.");

        object sqliteMonthsBetweenPreview = BuildViewSqlPreview(
            "SELECT MONTHS_BETWEEN(end_date, start_date) AS months_open FROM tickets",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteMonthsBetweenPreview, "CanConvert"), "Oracle MONTHS_BETWEEN should convert to SQLite.");
        string sqliteMonthsBetweenSql = (string)GetProperty(sqliteMonthsBetweenPreview, "ConvertedSql");
        AssertContains(sqliteMonthsBetweenSql, "((CAST(strftime('%Y', end_date) AS INTEGER) - CAST(strftime('%Y', start_date) AS INTEGER)) * 12 + (CAST(strftime('%m', end_date) AS INTEGER) - CAST(strftime('%m', start_date) AS INTEGER)))", "Converted SQLite SQL should calculate month difference from year and month parts.");

        object pgMonthsBetweenPreview = BuildViewSqlPreview(
            "SELECT MONTHS_BETWEEN(end_date, start_date) AS months_open FROM tickets",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgMonthsBetweenPreview, "CanConvert"), "Oracle MONTHS_BETWEEN should convert to PostgreSQL.");
        string pgMonthsBetweenSql = (string)GetProperty(pgMonthsBetweenPreview, "ConvertedSql");
        AssertContains(pgMonthsBetweenSql, "CAST((EXTRACT(YEAR FROM AGE(end_date, start_date)) * 12) + EXTRACT(MONTH FROM AGE(end_date, start_date)) AS INTEGER)", "Converted PostgreSQL SQL should use AGE for month difference.");
        AssertNotContains(pgMonthsBetweenSql, "MONTHS_BETWEEN", "Converted PostgreSQL SQL should remove MONTHS_BETWEEN.");

        object mssqlDateAddPreview = BuildViewSqlPreview(
            "SELECT DATE_ADD(created_at, INTERVAL 7 DAY) AS expires_at FROM sessions",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlDateAddPreview, "CanConvert"), "MySQL DATE_ADD should convert to SQL Server.");
        string mssqlDateAddSql = (string)GetProperty(mssqlDateAddPreview, "ConvertedSql");
        AssertContains(mssqlDateAddSql, "DATEADD(day, 7, created_at)", "Converted SQL Server SQL should use DATEADD.");

        object sqliteDateAddPreview = BuildViewSqlPreview(
            "SELECT DATEADD(day, -3, due_at) AS reminder_at FROM tasks",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteDateAddPreview, "CanConvert"), "SQL Server DATEADD should convert to SQLite.");
        string sqliteDateAddSql = (string)GetProperty(sqliteDateAddPreview, "ConvertedSql");
        AssertContains(sqliteDateAddSql, "date(due_at, '-3 day')", "Converted SQLite SQL should use date modifier.");

        object pgDateAddPreview = BuildViewSqlPreview(
            "SELECT DATEADD(day, 14, created_at) AS expires_at FROM sessions",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgDateAddPreview, "CanConvert"), "SQL Server DATEADD should convert to PostgreSQL.");
        string pgDateAddSql = (string)GetProperty(pgDateAddPreview, "ConvertedSql");
        AssertContains(pgDateAddSql, "created_at + INTERVAL '14 day'", "Converted PostgreSQL SQL should use interval addition.");

        object pgVariableDateAddPreview = BuildViewSqlPreview(
            "SELECT DATEADD(day, retry_days, created_at) AS retry_at FROM sessions",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgVariableDateAddPreview, "CanConvert"), "SQL Server DATEADD with expression amount should convert to PostgreSQL.");
        string pgVariableDateAddSql = (string)GetProperty(pgVariableDateAddPreview, "ConvertedSql");
        AssertContains(pgVariableDateAddSql, "created_at + (retry_days * INTERVAL '1 day')", "Converted PostgreSQL SQL should multiply expression interval amounts.");
        AssertNotContains(pgVariableDateAddSql, "DATEADD", "Converted PostgreSQL SQL should remove DATEADD with expression amount.");

        object mssqlVariableDateAddPreview = BuildViewSqlPreview(
            "SELECT DATE_ADD(created_at, INTERVAL grace_days DAY) AS expires_at FROM sessions",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlVariableDateAddPreview, "CanConvert"), "MySQL DATE_ADD with expression amount should convert to SQL Server.");
        string mssqlVariableDateAddSql = (string)GetProperty(mssqlVariableDateAddPreview, "ConvertedSql");
        AssertContains(mssqlVariableDateAddSql, "DATEADD(day, grace_days, created_at)", "Converted SQL Server SQL should keep expression interval amounts.");

        object sqliteVariableDateAddPreview = BuildViewSqlPreview(
            "SELECT DATEADD(hour, reminder_hours, started_at) AS reminder_at FROM jobs",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteVariableDateAddPreview, "CanConvert"), "SQL Server DATEADD with expression amount should convert to SQLite.");
        string sqliteVariableDateAddSql = (string)GetProperty(sqliteVariableDateAddPreview, "ConvertedSql");
        AssertContains(sqliteVariableDateAddSql, "datetime(started_at, CASE WHEN reminder_hours < 0 THEN CAST(reminder_hours AS TEXT) || ' hour' ELSE '+' || CAST(reminder_hours AS TEXT) || ' hour' END)", "Converted SQLite SQL should build a dynamic date modifier for expression interval amounts.");

        object mssqlMonthAddPreview = BuildViewSqlPreview(
            "SELECT DATE_ADD(created_at, INTERVAL 2 MONTH) AS expires_at FROM sessions",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlMonthAddPreview, "CanConvert"), "MySQL DATE_ADD month should convert to SQL Server.");
        string mssqlMonthAddSql = (string)GetProperty(mssqlMonthAddPreview, "ConvertedSql");
        AssertContains(mssqlMonthAddSql, "DATEADD(month, 2, created_at)", "Converted SQL Server SQL should use DATEADD month.");

        object mssqlQuarterAddPreview = BuildViewSqlPreview(
            "SELECT DATE_ADD(created_at, INTERVAL 1 QUARTER) AS next_quarter_at FROM sessions",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlQuarterAddPreview, "CanConvert"), "MySQL DATE_ADD quarter should convert to SQL Server.");
        string mssqlQuarterAddSql = (string)GetProperty(mssqlQuarterAddPreview, "ConvertedSql");
        AssertContains(mssqlQuarterAddSql, "DATEADD(quarter, 1, created_at)", "Converted SQL Server SQL should use DATEADD quarter.");

        object sqliteQuarterAddPreview = BuildViewSqlPreview(
            "SELECT DATEADD(quarter, 2, created_at) AS next_half_at FROM sessions",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteQuarterAddPreview, "CanConvert"), "SQL Server DATEADD quarter should convert to SQLite.");
        string sqliteQuarterAddSql = (string)GetProperty(sqliteQuarterAddPreview, "ConvertedSql");
        AssertContains(sqliteQuarterAddSql, "date(created_at, '+6 month')", "Converted SQLite SQL should convert quarter to months.");

        object oracleWeekAddPreview = BuildViewSqlPreview(
            "SELECT DATE_ADD(created_at, INTERVAL 2 WEEK) AS review_at FROM sessions",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleWeekAddPreview, "CanConvert"), "MySQL DATE_ADD week should convert to Oracle.");
        string oracleWeekAddSql = (string)GetProperty(oracleWeekAddPreview, "ConvertedSql");
        AssertContains(oracleWeekAddSql, "created_at + 14", "Converted Oracle SQL should convert weeks to days.");

        object sqliteHourAddPreview = BuildViewSqlPreview(
            "SELECT DATEADD(hour, 6, started_at) AS reminder_at FROM jobs",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteHourAddPreview, "CanConvert"), "SQL Server DATEADD hour should convert to SQLite.");
        string sqliteHourAddSql = (string)GetProperty(sqliteHourAddPreview, "ConvertedSql");
        AssertContains(sqliteHourAddSql, "datetime(started_at, '+6 hour')", "Converted SQLite SQL should use datetime hour modifier.");

        object oracleYearAddPreview = BuildViewSqlPreview(
            "SELECT DATE_ADD(created_at, INTERVAL 1 YEAR) AS renew_at FROM contracts",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleYearAddPreview, "CanConvert"), "MySQL DATE_ADD year should convert to Oracle.");
        string oracleYearAddSql = (string)GetProperty(oracleYearAddPreview, "ConvertedSql");
        AssertContains(oracleYearAddSql, "ADD_MONTHS(created_at, 12)", "Converted Oracle SQL should use ADD_MONTHS for year addition.");

        object mssqlAddMonthsPreview = BuildViewSqlPreview(
            "SELECT ADD_MONTHS(created_at, 3) AS renew_at FROM contracts",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlAddMonthsPreview, "CanConvert"), "Oracle ADD_MONTHS should convert to SQL Server.");
        string mssqlAddMonthsSql = (string)GetProperty(mssqlAddMonthsPreview, "ConvertedSql");
        AssertContains(mssqlAddMonthsSql, "DATEADD(month, 3, created_at)", "Converted SQL Server SQL should use DATEADD month.");

        object mysqlAddMonthsPreview = BuildViewSqlPreview(
            "SELECT ADD_MONTHS(created_at, -2) AS reminder_at FROM contracts",
            "oracle",
            "mysql");
        Assert((bool)GetProperty(mysqlAddMonthsPreview, "CanConvert"), "Oracle ADD_MONTHS should convert to MySQL.");
        string mysqlAddMonthsSql = (string)GetProperty(mysqlAddMonthsPreview, "ConvertedSql");
        AssertContains(mysqlAddMonthsSql, "DATE_ADD(created_at, INTERVAL -2 MONTH)", "Converted MySQL SQL should use DATE_ADD month.");

        object sqliteAddMonthsPreview = BuildViewSqlPreview(
            "SELECT ADD_MONTHS(created_at, 1) AS next_month_at FROM contracts",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteAddMonthsPreview, "CanConvert"), "Oracle ADD_MONTHS should convert to SQLite.");
        string sqliteAddMonthsSql = (string)GetProperty(sqliteAddMonthsPreview, "ConvertedSql");
        AssertContains(sqliteAddMonthsSql, "date(created_at, '+1 month')", "Converted SQLite SQL should use date month modifier.");

        object pgAddMonthsPreview = BuildViewSqlPreview(
            "SELECT ADD_MONTHS(created_at, billing_offset) AS billing_at FROM contracts",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgAddMonthsPreview, "CanConvert"), "Oracle ADD_MONTHS with expression offset should convert to PostgreSQL.");
        string pgAddMonthsSql = (string)GetProperty(pgAddMonthsPreview, "ConvertedSql");
        AssertContains(pgAddMonthsSql, "created_at + (billing_offset * INTERVAL '1 month')", "Converted PostgreSQL SQL should multiply the offset by a month interval.");
        AssertNotContains(pgAddMonthsSql, "ADD_MONTHS", "Converted PostgreSQL SQL should remove ADD_MONTHS.");

        object mssqlDateSubPreview = BuildViewSqlPreview(
            "SELECT DATE_SUB(expires_at, INTERVAL 7 DAY) AS warning_at FROM sessions",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlDateSubPreview, "CanConvert"), "MySQL DATE_SUB should convert to SQL Server.");
        string mssqlDateSubSql = (string)GetProperty(mssqlDateSubPreview, "ConvertedSql");
        AssertContains(mssqlDateSubSql, "DATEADD(day, -7, expires_at)", "Converted SQL Server SQL should use negative DATEADD.");

        object sqliteDateSubPreview = BuildViewSqlPreview(
            "SELECT DATE_SUB(expires_at, INTERVAL 7 DAY) AS warning_at FROM sessions",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteDateSubPreview, "CanConvert"), "MySQL DATE_SUB should convert to SQLite.");
        string sqliteDateSubSql = (string)GetProperty(sqliteDateSubPreview, "ConvertedSql");
        AssertContains(sqliteDateSubSql, "date(expires_at, '-7 day')", "Converted SQLite SQL should use a negative date modifier.");

        object pgYearPreview = BuildViewSqlPreview(
            "SELECT YEAR(created_at) AS created_year FROM orders",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgYearPreview, "CanConvert"), "MySQL YEAR should convert to PostgreSQL.");
        string pgYearSql = (string)GetProperty(pgYearPreview, "ConvertedSql");
        AssertContains(pgYearSql, "EXTRACT(YEAR FROM created_at)", "Converted PostgreSQL SQL should use EXTRACT.");

        object sqliteDatePartPreview = BuildViewSqlPreview(
            "SELECT DATEPART(month, created_at) AS created_month FROM orders",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteDatePartPreview, "CanConvert"), "SQL Server DATEPART should convert to SQLite.");
        string sqliteDatePartSql = (string)GetProperty(sqliteDatePartPreview, "ConvertedSql");
        AssertContains(sqliteDatePartSql, "CAST(strftime('%m', created_at) AS INTEGER)", "Converted SQLite SQL should use strftime.");

        object sqliteNestedDatePartPreview = BuildViewSqlPreview(
            "SELECT DATEPART(day, DATEADD(day, 1, created_at)) AS next_day, 'DATEPART(day, DATEADD(day, 1, created_at))' AS literal_note FROM orders",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteNestedDatePartPreview, "CanConvert"), "Nested SQL Server DATEPART should convert while preserving literals.");
        string sqliteNestedDatePartSql = (string)GetProperty(sqliteNestedDatePartPreview, "ConvertedSql");
        AssertContains(sqliteNestedDatePartSql, "CAST(strftime('%d', date(created_at, '+1 day')) AS INTEGER) AS next_day", "Converted SQLite SQL should convert nested DATEPART expression.");
        AssertContains(sqliteNestedDatePartSql, "'DATEPART(day, DATEADD(day, 1, created_at))' AS literal_note", "Converted SQLite SQL should preserve DATEPART text inside string literals.");

        object mysqlExtractPreview = BuildViewSqlPreview(
            "SELECT EXTRACT(DAY FROM created_at) AS created_day FROM orders",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlExtractPreview, "CanConvert"), "PostgreSQL EXTRACT should convert to MySQL.");
        string mysqlExtractSql = (string)GetProperty(mysqlExtractPreview, "ConvertedSql");
        AssertContains(mysqlExtractSql, "DAY(created_at)", "Converted MySQL SQL should use DAY().");

        object mysqlQuarterPreview = BuildViewSqlPreview(
            "SELECT DATE_PART('quarter', created_at) AS created_quarter FROM orders",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlQuarterPreview, "CanConvert"), "PostgreSQL DATE_PART quarter should convert to MySQL.");
        string mysqlQuarterSql = (string)GetProperty(mysqlQuarterPreview, "ConvertedSql");
        AssertContains(mysqlQuarterSql, "QUARTER(created_at)", "Converted MySQL SQL should use QUARTER().");

        object sqliteQuarterPreview = BuildViewSqlPreview(
            "SELECT DATEPART(quarter, created_at) AS created_quarter FROM orders",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteQuarterPreview, "CanConvert"), "SQL Server DATEPART quarter should convert to SQLite.");
        string sqliteQuarterSql = (string)GetProperty(sqliteQuarterPreview, "ConvertedSql");
        AssertContains(sqliteQuarterSql, "CAST(((CAST(strftime('%m', created_at) AS INTEGER) + 2) / 3) AS INTEGER)", "Converted SQLite SQL should calculate quarter from month.");

        object pgDayOfYearPreview = BuildViewSqlPreview(
            "SELECT DAYOFYEAR(created_at) AS created_doy FROM orders",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgDayOfYearPreview, "CanConvert"), "MySQL DAYOFYEAR should convert to PostgreSQL.");
        string pgDayOfYearSql = (string)GetProperty(pgDayOfYearPreview, "ConvertedSql");
        AssertContains(pgDayOfYearSql, "EXTRACT(DOY FROM created_at)", "Converted PostgreSQL SQL should use EXTRACT DOY.");

        object sqliteDayOfYearPreview = BuildViewSqlPreview(
            "SELECT EXTRACT(DOY FROM created_at) AS created_doy FROM orders",
            "postgresql",
            "sqlite");
        Assert((bool)GetProperty(sqliteDayOfYearPreview, "CanConvert"), "PostgreSQL EXTRACT DOY should convert to SQLite.");
        string sqliteDayOfYearSql = (string)GetProperty(sqliteDayOfYearPreview, "ConvertedSql");
        AssertContains(sqliteDayOfYearSql, "CAST(strftime('%j', created_at) AS INTEGER)", "Converted SQLite SQL should use day-of-year strftime.");

        object oracleQuarterPreview = BuildViewSqlPreview(
            "SELECT QUARTER(created_at) AS created_quarter FROM orders",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleQuarterPreview, "CanConvert"), "MySQL QUARTER should convert to Oracle.");
        string oracleQuarterSql = (string)GetProperty(oracleQuarterPreview, "ConvertedSql");
        AssertContains(oracleQuarterSql, "TO_NUMBER(TO_CHAR(created_at, 'Q'))", "Converted Oracle SQL should calculate quarter with TO_CHAR.");

        object pgWeekPreview = BuildViewSqlPreview(
            "SELECT DATEPART(week, created_at) AS created_week FROM orders",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgWeekPreview, "CanConvert"), "SQL Server DATEPART week should convert to PostgreSQL.");
        string pgWeekSql = (string)GetProperty(pgWeekPreview, "ConvertedSql");
        AssertContains(pgWeekSql, "EXTRACT(WEEK FROM created_at)", "Converted PostgreSQL SQL should use EXTRACT WEEK.");

        object sqliteWeekdayPreview = BuildViewSqlPreview(
            "SELECT DAYOFWEEK(created_at) AS created_weekday FROM orders",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteWeekdayPreview, "CanConvert"), "MySQL DAYOFWEEK should convert to SQLite.");
        string sqliteWeekdaySql = (string)GetProperty(sqliteWeekdayPreview, "ConvertedSql");
        AssertContains(sqliteWeekdaySql, "(CAST(strftime('%w', created_at) AS INTEGER) + 1)", "Converted SQLite SQL should preserve Sunday-based weekday numbering.");

        object oracleWeekPreview = BuildViewSqlPreview(
            "SELECT DATE_PART('week', created_at) AS created_week FROM orders",
            "postgresql",
            "oracle");
        Assert((bool)GetProperty(oracleWeekPreview, "CanConvert"), "PostgreSQL DATE_PART week should convert to Oracle.");
        string oracleWeekSql = (string)GetProperty(oracleWeekPreview, "ConvertedSql");
        AssertContains(oracleWeekSql, "TO_NUMBER(TO_CHAR(created_at, 'WW'))", "Converted Oracle SQL should calculate week with TO_CHAR.");

        object mssqlDatePartFunctionPreview = BuildViewSqlPreview(
            "SELECT DATE_PART('hour', created_at) AS created_hour FROM orders",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlDatePartFunctionPreview, "CanConvert"), "PostgreSQL DATE_PART should convert to SQL Server.");
        string mssqlDatePartFunctionSql = (string)GetProperty(mssqlDatePartFunctionPreview, "ConvertedSql");
        AssertContains(mssqlDatePartFunctionSql, "DATEPART(hour, created_at)", "Converted SQL Server SQL should use DATEPART for time parts.");
        AssertNotContains(mssqlDatePartFunctionSql, "DATE_PART", "Converted SQL Server SQL should remove DATE_PART.");

        object sqliteDatePartFunctionPreview = BuildViewSqlPreview(
            "SELECT DATE_PART('second', created_at) AS created_second FROM orders",
            "postgresql",
            "sqlite");
        Assert((bool)GetProperty(sqliteDatePartFunctionPreview, "CanConvert"), "PostgreSQL DATE_PART should convert to SQLite.");
        string sqliteDatePartFunctionSql = (string)GetProperty(sqliteDatePartFunctionPreview, "ConvertedSql");
        AssertContains(sqliteDatePartFunctionSql, "CAST(strftime('%S', created_at) AS INTEGER)", "Converted SQLite SQL should use second strftime.");

        object pgHourPreview = BuildViewSqlPreview(
            "SELECT HOUR(created_at) AS created_hour FROM orders",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgHourPreview, "CanConvert"), "MySQL HOUR should convert to PostgreSQL.");
        string pgHourSql = (string)GetProperty(pgHourPreview, "ConvertedSql");
        AssertContains(pgHourSql, "EXTRACT(HOUR FROM created_at)", "Converted PostgreSQL SQL should use EXTRACT HOUR.");

        object sqliteMinutePreview = BuildViewSqlPreview(
            "SELECT DATEPART(minute, created_at) AS created_minute FROM orders",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteMinutePreview, "CanConvert"), "SQL Server DATEPART minute should convert to SQLite.");
        string sqliteMinuteSql = (string)GetProperty(sqliteMinutePreview, "ConvertedSql");
        AssertContains(sqliteMinuteSql, "CAST(strftime('%M', created_at) AS INTEGER)", "Converted SQLite SQL should use minute strftime.");

        object mysqlSecondPreview = BuildViewSqlPreview(
            "SELECT EXTRACT(SECOND FROM created_at) AS created_second FROM orders",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlSecondPreview, "CanConvert"), "PostgreSQL EXTRACT SECOND should convert to MySQL.");
        string mysqlSecondSql = (string)GetProperty(mysqlSecondPreview, "ConvertedSql");
        AssertContains(mysqlSecondSql, "SECOND(created_at)", "Converted MySQL SQL should use SECOND().");

        object pgIfPreview = BuildViewSqlPreview(
            "SELECT IF(is_active = 1, 'Y', 'N') AS active_text FROM users",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgIfPreview, "CanConvert"), "MySQL IF should convert to PostgreSQL.");
        string pgIfSql = (string)GetProperty(pgIfPreview, "ConvertedSql");
        AssertContains(pgIfSql, "CASE WHEN is_active = 1 THEN 'Y' ELSE 'N' END", "Converted PostgreSQL SQL should use CASE.");

        object mysqlIifPreview = BuildViewSqlPreview(
            "SELECT IIF(score >= 60, 'pass', 'fail') AS status_text FROM exams",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlIifPreview, "CanConvert"), "SQL Server IIF should convert to MySQL.");
        string mysqlIifSql = (string)GetProperty(mysqlIifPreview, "ConvertedSql");
        AssertContains(mysqlIifSql, "IF(score >= 60, 'pass', 'fail')", "Converted MySQL SQL should use IF.");

        object pgNestedIifPreview = BuildViewSqlPreview(
            "SELECT IIF(ABS(score) >= 60, 'pass', 'fail') AS status_text, 'IIF(ABS(score) >= 60, ''pass'', ''fail'')' AS literal_note FROM exams",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgNestedIifPreview, "CanConvert"), "Nested SQL Server IIF should convert while preserving literals.");
        string pgNestedIifSql = (string)GetProperty(pgNestedIifPreview, "ConvertedSql");
        AssertContains(pgNestedIifSql, "CASE WHEN ABS(score) >= 60 THEN 'pass' ELSE 'fail' END AS status_text", "Converted PostgreSQL SQL should convert nested IIF condition.");
        AssertContains(pgNestedIifSql, "'IIF(ABS(score) >= 60, ''pass'', ''fail'')' AS literal_note", "Converted PostgreSQL SQL should preserve IIF text inside string literals.");

        object sqliteIifPreview = BuildViewSqlPreview(
            "SELECT IIF(score >= 60, 'pass', 'fail') AS status_text FROM exams",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteIifPreview, "CanConvert"), "SQL Server IIF should convert to SQLite.");
        string sqliteIifSql = (string)GetProperty(sqliteIifPreview, "ConvertedSql");
        AssertContains(sqliteIifSql, "CASE WHEN score >= 60 THEN 'pass' ELSE 'fail' END", "Converted SQLite SQL should use CASE.");

        object pgDecodePreview = BuildViewSqlPreview(
            "SELECT DECODE(status, 'A', 'active', 'I', 'inactive', 'other') AS status_text FROM users",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgDecodePreview, "CanConvert"), "Oracle DECODE should convert to PostgreSQL.");
        string pgDecodeSql = (string)GetProperty(pgDecodePreview, "ConvertedSql");
        AssertContains(pgDecodeSql, "CASE status WHEN 'A' THEN 'active' WHEN 'I' THEN 'inactive' ELSE 'other' END", "Converted PostgreSQL SQL should use CASE for DECODE.");

        object mssqlDecodePreview = BuildViewSqlPreview(
            "SELECT DECODE(status, 'A', 'active', 'I', 'inactive') AS status_text FROM users",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlDecodePreview, "CanConvert"), "Oracle DECODE should convert to SQL Server.");
        string mssqlDecodeSql = (string)GetProperty(mssqlDecodePreview, "ConvertedSql");
        AssertContains(mssqlDecodeSql, "CASE status WHEN 'A' THEN 'active' WHEN 'I' THEN 'inactive' END", "Converted SQL Server SQL should use CASE for DECODE without default.");

        object mysqlDecodePreview = BuildViewSqlPreview(
            "SELECT DECODE(status, 'A', 'active', 'I', 'inactive', 'other') AS status_text FROM users",
            "oracle",
            "mysql");
        Assert((bool)GetProperty(mysqlDecodePreview, "CanConvert"), "Oracle DECODE should convert to MySQL.");
        string mysqlDecodeSql = (string)GetProperty(mysqlDecodePreview, "ConvertedSql");
        AssertContains(mysqlDecodeSql, "CASE status WHEN 'A' THEN 'active' WHEN 'I' THEN 'inactive' ELSE 'other' END", "Converted MySQL SQL should use CASE for DECODE.");

        object mysqlNvl2Preview = BuildViewSqlPreview(
            "SELECT NVL2(closed_at, 'closed', 'open') AS state_text FROM tickets",
            "oracle",
            "mysql");
        Assert((bool)GetProperty(mysqlNvl2Preview, "CanConvert"), "Oracle NVL2 should convert to MySQL.");
        string mysqlNvl2Sql = (string)GetProperty(mysqlNvl2Preview, "ConvertedSql");
        AssertContains(mysqlNvl2Sql, "IF(closed_at IS NOT NULL, 'closed', 'open')", "Converted MySQL SQL should use IF for NVL2.");

        object mssqlNvl2Preview = BuildViewSqlPreview(
            "SELECT NVL2(closed_at, 'closed', 'open') AS state_text FROM tickets",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlNvl2Preview, "CanConvert"), "Oracle NVL2 should convert to SQL Server.");
        string mssqlNvl2Sql = (string)GetProperty(mssqlNvl2Preview, "ConvertedSql");
        AssertContains(mssqlNvl2Sql, "IIF(closed_at IS NOT NULL, 'closed', 'open')", "Converted SQL Server SQL should use IIF for NVL2.");

        object pgNvl2Preview = BuildViewSqlPreview(
            "SELECT NVL2(closed_at, 'closed', 'open') AS state_text FROM tickets",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgNvl2Preview, "CanConvert"), "Oracle NVL2 should convert to PostgreSQL.");
        string pgNvl2Sql = (string)GetProperty(pgNvl2Preview, "ConvertedSql");
        AssertContains(pgNvl2Sql, "CASE WHEN closed_at IS NOT NULL THEN 'closed' ELSE 'open' END", "Converted PostgreSQL SQL should use CASE for NVL2.");

        object pgNvl2LiteralPreview = BuildViewSqlPreview(
            "SELECT NVL2(closed_at, 'closed', 'NVL2(open)') AS state_text, 'NVL2(note)' AS literal_note FROM tickets",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgNvl2LiteralPreview, "CanConvert"), "Oracle NVL2 should convert while preserving literals.");
        string pgNvl2LiteralSql = (string)GetProperty(pgNvl2LiteralPreview, "ConvertedSql");
        AssertContains(pgNvl2LiteralSql, "CASE WHEN closed_at IS NOT NULL THEN 'closed' ELSE 'NVL2(open)' END", "Converted PostgreSQL SQL should convert NVL2 function only.");
        AssertContains(pgNvl2LiteralSql, "'NVL2(note)' AS literal_note", "Converted PostgreSQL SQL should preserve NVL2 text inside string literals.");

        object pgNvlLiteralPreview = BuildViewSqlPreview(
            "SELECT NVL(display_name, 'NVL(fallback)') AS clean_name, 'NVL(note)' AS literal_note FROM users",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgNvlLiteralPreview, "CanConvert"), "Oracle NVL should convert while preserving literals.");
        string pgNvlLiteralSql = (string)GetProperty(pgNvlLiteralPreview, "ConvertedSql");
        AssertContains(pgNvlLiteralSql, "COALESCE(display_name, 'NVL(fallback)')", "Converted PostgreSQL SQL should convert NVL function only.");
        AssertContains(pgNvlLiteralSql, "'NVL(note)' AS literal_note", "Converted PostgreSQL SQL should preserve NVL text inside string literals.");

        object pgIfNullLiteralPreview = BuildViewSqlPreview(
            "SELECT IFNULL(display_name, 'IFNULL(fallback)') AS clean_name, 'IFNULL(note)' AS literal_note FROM users",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgIfNullLiteralPreview, "CanConvert"), "MySQL IFNULL should convert while preserving literals.");
        string pgIfNullLiteralSql = (string)GetProperty(pgIfNullLiteralPreview, "ConvertedSql");
        AssertContains(pgIfNullLiteralSql, "COALESCE(display_name, 'IFNULL(fallback)')", "Converted PostgreSQL SQL should convert IFNULL function only.");
        AssertContains(pgIfNullLiteralSql, "'IFNULL(note)' AS literal_note", "Converted PostgreSQL SQL should preserve IFNULL text inside string literals.");

        object pgIsNullLiteralPreview = BuildViewSqlPreview(
            "SELECT ISNULL(deleted_at) AS is_deleted, 'ISNULL(note)' AS literal_note FROM users",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgIsNullLiteralPreview, "CanConvert"), "MySQL ISNULL predicate should convert while preserving literals.");
        string pgIsNullLiteralSql = (string)GetProperty(pgIsNullLiteralPreview, "ConvertedSql");
        AssertContains(pgIsNullLiteralSql, "deleted_at IS NULL AS is_deleted", "Converted PostgreSQL SQL should convert ISNULL predicate only.");
        AssertContains(pgIsNullLiteralSql, "'ISNULL(note)' AS literal_note", "Converted PostgreSQL SQL should preserve ISNULL text inside string literals.");

        object mssqlCeilPreview = BuildViewSqlPreview(
            "SELECT CEIL(total_amount / 100.0) AS bill_units FROM orders",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlCeilPreview, "CanConvert"), "Oracle CEIL should convert to SQL Server.");
        string mssqlCeilSql = (string)GetProperty(mssqlCeilPreview, "ConvertedSql");
        AssertContains(mssqlCeilSql, "CEILING(total_amount / 100.0)", "Converted SQL Server SQL should use CEILING.");

        object mssqlCeilLiteralPreview = BuildViewSqlPreview(
            "SELECT CEIL(total_amount / 100.0) AS bill_units, 'CEIL(note)' AS literal_note FROM orders",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlCeilLiteralPreview, "CanConvert"), "Oracle CEIL should convert while preserving literals.");
        string mssqlCeilLiteralSql = (string)GetProperty(mssqlCeilLiteralPreview, "ConvertedSql");
        AssertContains(mssqlCeilLiteralSql, "CEILING(total_amount / 100.0) AS bill_units", "Converted SQL Server SQL should convert CEIL function only.");
        AssertContains(mssqlCeilLiteralSql, "'CEIL(note)' AS literal_note", "Converted SQL Server SQL should preserve CEIL text inside string literals.");

        object mssqlToNumberPreview = BuildViewSqlPreview(
            "SELECT TO_NUMBER(total_text) AS total_value, TO_NUMBER(rate_text, '999D99') AS rate_value FROM orders",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlToNumberPreview, "CanConvert"), "Oracle TO_NUMBER should convert to SQL Server.");
        string mssqlToNumberSql = (string)GetProperty(mssqlToNumberPreview, "ConvertedSql");
        AssertContains(mssqlToNumberSql, "CAST(total_text AS decimal(18,4))", "Converted SQL Server SQL should cast TO_NUMBER value.");
        AssertContains(mssqlToNumberSql, "CAST(rate_text AS decimal(18,4))", "Converted SQL Server SQL should cast formatted TO_NUMBER value.");
        AssertNotContains(mssqlToNumberSql, "TO_NUMBER", "Converted SQL Server SQL should remove TO_NUMBER.");

        object pgNestedToNumberPreview = BuildViewSqlPreview(
            "SELECT TO_NUMBER(REPLACE(total_text, ',', ''), '999999D99') AS total_value, 'TO_NUMBER(REPLACE(total_text, '','', ''''), ''999999D99'')' AS literal_note FROM orders",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgNestedToNumberPreview, "CanConvert"), "Nested Oracle TO_NUMBER should convert while preserving literals.");
        string pgNestedToNumberSql = (string)GetProperty(pgNestedToNumberPreview, "ConvertedSql");
        AssertContains(pgNestedToNumberSql, "CAST(REPLACE(total_text, ',', '') AS numeric) AS total_value", "Converted PostgreSQL SQL should convert nested TO_NUMBER argument.");
        AssertContains(pgNestedToNumberSql, "'TO_NUMBER(REPLACE(total_text, '','', ''''), ''999999D99'')' AS literal_note", "Converted PostgreSQL SQL should preserve TO_NUMBER text inside string literals.");

        object sqliteToNumberPreview = BuildViewSqlPreview(
            "SELECT TO_NUMBER(total_text) AS total_value FROM orders",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteToNumberPreview, "CanConvert"), "Oracle TO_NUMBER should convert to SQLite.");
        string sqliteToNumberSql = (string)GetProperty(sqliteToNumberPreview, "ConvertedSql");
        AssertContains(sqliteToNumberSql, "CAST(total_text AS NUMERIC)", "Converted SQLite SQL should cast TO_NUMBER value.");
        AssertNotContains(sqliteToNumberSql, "TO_NUMBER", "Converted SQLite SQL should remove TO_NUMBER.");

        object oracleCeilingPreview = BuildViewSqlPreview(
            "SELECT CEILING(total_amount / 100.0) AS bill_units FROM orders",
            "mssql",
            "oracle");
        Assert((bool)GetProperty(oracleCeilingPreview, "CanConvert"), "SQL Server CEILING should convert to Oracle.");
        string oracleCeilingSql = (string)GetProperty(oracleCeilingPreview, "ConvertedSql");
        AssertContains(oracleCeilingSql, "CEIL(total_amount / 100.0)", "Converted Oracle SQL should use CEIL.");

        object pgCeilingPreview = BuildViewSqlPreview(
            "SELECT CEILING(total_amount / 100.0) AS bill_units FROM orders",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgCeilingPreview, "CanConvert"), "SQL Server CEILING should convert to PostgreSQL.");
        string pgCeilingSql = (string)GetProperty(pgCeilingPreview, "ConvertedSql");
        AssertContains(pgCeilingSql, "CEIL(total_amount / 100.0)", "Converted PostgreSQL SQL should use CEIL.");

        object mssqlTruncateNumberPreview = BuildViewSqlPreview(
            "SELECT TRUNCATE(total_amount, 2) AS truncated_total FROM orders",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlTruncateNumberPreview, "CanConvert"), "MySQL TRUNCATE should convert to SQL Server.");
        string mssqlTruncateNumberSql = (string)GetProperty(mssqlTruncateNumberPreview, "ConvertedSql");
        AssertContains(mssqlTruncateNumberSql, "ROUND(total_amount, 2, 1)", "Converted SQL Server SQL should use ROUND with truncate flag.");
        AssertNotContains(mssqlTruncateNumberSql, "TRUNCATE(", "Converted SQL Server SQL should remove MySQL TRUNCATE function.");

        object pgTruncateNumberPreview = BuildViewSqlPreview(
            "SELECT TRUNCATE(total_amount, 2) AS truncated_total FROM orders",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgTruncateNumberPreview, "CanConvert"), "MySQL TRUNCATE should convert to PostgreSQL.");
        string pgTruncateNumberSql = (string)GetProperty(pgTruncateNumberPreview, "ConvertedSql");
        AssertContains(pgTruncateNumberSql, "TRUNC(total_amount, 2)", "Converted PostgreSQL SQL should use TRUNC.");

        object pgNestedTruncateNumberPreview = BuildViewSqlPreview(
            "SELECT TRUNCATE(ABS(total_amount), 2) AS truncated_total, 'TRUNCATE(ABS(total_amount), 2)' AS literal_note FROM orders",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgNestedTruncateNumberPreview, "CanConvert"), "Nested MySQL TRUNCATE should convert while preserving literals.");
        string pgNestedTruncateNumberSql = (string)GetProperty(pgNestedTruncateNumberPreview, "ConvertedSql");
        AssertContains(pgNestedTruncateNumberSql, "TRUNC(ABS(total_amount), 2) AS truncated_total", "Converted PostgreSQL SQL should convert nested TRUNCATE arguments.");
        AssertContains(pgNestedTruncateNumberSql, "'TRUNCATE(ABS(total_amount), 2)' AS literal_note", "Converted PostgreSQL SQL should preserve TRUNCATE text inside string literals.");

        object oracleTruncateNumberPreview = BuildViewSqlPreview(
            "SELECT TRUNCATE(total_amount, 2) AS truncated_total FROM orders",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleTruncateNumberPreview, "CanConvert"), "MySQL TRUNCATE should convert to Oracle.");
        string oracleTruncateNumberSql = (string)GetProperty(oracleTruncateNumberPreview, "ConvertedSql");
        AssertContains(oracleTruncateNumberSql, "TRUNC(total_amount, 2)", "Converted Oracle SQL should use TRUNC.");

        object mysqlTruncatingRoundPreview = BuildViewSqlPreview(
            "SELECT ROUND(total_amount, 2, 1) AS truncated_total FROM orders",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlTruncatingRoundPreview, "CanConvert"), "SQL Server truncating ROUND should convert to MySQL.");
        string mysqlTruncatingRoundSql = (string)GetProperty(mysqlTruncatingRoundPreview, "ConvertedSql");
        AssertContains(mysqlTruncatingRoundSql, "TRUNCATE(total_amount, 2)", "Converted MySQL SQL should use TRUNCATE for truncating ROUND.");
        AssertNotContains(mysqlTruncatingRoundSql, "ROUND(total_amount, 2, 1)", "Converted MySQL SQL should remove SQL Server truncating ROUND.");

        object pgTruncatingRoundPreview = BuildViewSqlPreview(
            "SELECT ROUND(total_amount, 2, 1) AS truncated_total FROM orders",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgTruncatingRoundPreview, "CanConvert"), "SQL Server truncating ROUND should convert to PostgreSQL.");
        string pgTruncatingRoundSql = (string)GetProperty(pgTruncatingRoundPreview, "ConvertedSql");
        AssertContains(pgTruncatingRoundSql, "TRUNC(total_amount, 2)", "Converted PostgreSQL SQL should use TRUNC for truncating ROUND.");

        object oracleTruncatingRoundPreview = BuildViewSqlPreview(
            "SELECT ROUND(total_amount, 2, 1) AS truncated_total FROM orders",
            "mssql",
            "oracle");
        Assert((bool)GetProperty(oracleTruncatingRoundPreview, "CanConvert"), "SQL Server truncating ROUND should convert to Oracle.");
        string oracleTruncatingRoundSql = (string)GetProperty(oracleTruncatingRoundPreview, "ConvertedSql");
        AssertContains(oracleTruncatingRoundSql, "TRUNC(total_amount, 2)", "Converted Oracle SQL should use TRUNC for truncating ROUND.");

        object mssqlModPreview = BuildViewSqlPreview(
            "SELECT MOD(order_no, 10) AS shard_no FROM orders",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlModPreview, "CanConvert"), "MOD should convert to SQL Server modulo operator.");
        string mssqlModSql = (string)GetProperty(mssqlModPreview, "ConvertedSql");
        AssertContains(mssqlModSql, "(order_no % 10)", "Converted SQL Server SQL should use modulo operator.");

        object mssqlNestedModPreview = BuildViewSqlPreview(
            "SELECT MOD(ABS(order_no), 10) AS shard_no, 'MOD(note)' AS literal_note FROM orders",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlNestedModPreview, "CanConvert"), "Nested MOD should convert to SQL Server modulo operator.");
        string mssqlNestedModSql = (string)GetProperty(mssqlNestedModPreview, "ConvertedSql");
        AssertContains(mssqlNestedModSql, "(ABS(order_no) % 10)", "Converted SQL Server SQL should convert nested MOD arguments.");
        AssertContains(mssqlNestedModSql, "'MOD(note)' AS literal_note", "Converted SQL Server SQL should preserve MOD text inside string literals.");

        object mssqlGreatestPreview = BuildViewSqlPreview(
            "SELECT GREATEST(score, passing_score) AS effective_score FROM exams",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlGreatestPreview, "CanConvert"), "GREATEST should convert to SQL Server CASE expression.");
        string mssqlGreatestSql = (string)GetProperty(mssqlGreatestPreview, "ConvertedSql");
        AssertContains(mssqlGreatestSql, "(CASE WHEN score >= passing_score THEN score ELSE passing_score END)", "Converted SQL Server SQL should use CASE for GREATEST.");

        object mssqlLeastPreview = BuildViewSqlPreview(
            "SELECT LEAST(quantity, stock_limit) AS capped_quantity FROM inventory",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlLeastPreview, "CanConvert"), "LEAST should convert to SQL Server CASE expression.");
        string mssqlLeastSql = (string)GetProperty(mssqlLeastPreview, "ConvertedSql");
        AssertContains(mssqlLeastSql, "(CASE WHEN quantity <= stock_limit THEN quantity ELSE stock_limit END)", "Converted SQL Server SQL should use CASE for LEAST.");

        object sqliteGreatestPreview = BuildViewSqlPreview(
            "SELECT GREATEST(score, passing_score) AS effective_score FROM exams",
            "postgresql",
            "sqlite");
        Assert((bool)GetProperty(sqliteGreatestPreview, "CanConvert"), "GREATEST should convert to SQLite CASE expression.");
        string sqliteGreatestSql = (string)GetProperty(sqliteGreatestPreview, "ConvertedSql");
        AssertContains(sqliteGreatestSql, "(CASE WHEN score >= passing_score THEN score ELSE passing_score END)", "Converted SQLite SQL should use CASE for GREATEST.");
        AssertNotContains(sqliteGreatestSql, "GREATEST", "Converted SQLite SQL should remove GREATEST.");

        object sqliteLeastPreview = BuildViewSqlPreview(
            "SELECT LEAST(quantity, stock_limit) AS capped_quantity FROM inventory",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteLeastPreview, "CanConvert"), "LEAST should convert to SQLite CASE expression.");
        string sqliteLeastSql = (string)GetProperty(sqliteLeastPreview, "ConvertedSql");
        AssertContains(sqliteLeastSql, "(CASE WHEN quantity <= stock_limit THEN quantity ELSE stock_limit END)", "Converted SQLite SQL should use CASE for LEAST.");
        AssertNotContains(sqliteLeastSql, "LEAST", "Converted SQLite SQL should remove LEAST.");

        object pgGreatestPreview = BuildViewSqlPreview(
            "SELECT GREATEST(score, passing_score) AS effective_score FROM exams",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgGreatestPreview, "CanConvert"), "GREATEST should stay portable for PostgreSQL.");
        string pgGreatestSql = (string)GetProperty(pgGreatestPreview, "ConvertedSql");
        AssertContains(pgGreatestSql, "GREATEST(score, passing_score)", "Converted PostgreSQL SQL should keep native GREATEST.");

        object mssqlBooleanPreview = BuildViewSqlPreview(
            "SELECT id FROM feature_flags WHERE enabled = TRUE AND archived = FALSE AND label = 'TRUE'",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlBooleanPreview, "CanConvert"), "Boolean literals should convert to SQL Server numeric literals.");
        string mssqlBooleanSql = (string)GetProperty(mssqlBooleanPreview, "ConvertedSql");
        AssertContains(mssqlBooleanSql, "enabled = 1", "Converted SQL Server SQL should use 1 for TRUE.");
        AssertContains(mssqlBooleanSql, "archived = 0", "Converted SQL Server SQL should use 0 for FALSE.");
        AssertContains(mssqlBooleanSql, "label = 'TRUE'", "Converted SQL Server SQL should preserve string literals.");

        object oracleBooleanPreview = BuildViewSqlPreview(
            "SELECT FALSE AS is_deleted, TRUE AS is_active FROM users",
            "sqlite",
            "oracle");
        Assert((bool)GetProperty(oracleBooleanPreview, "CanConvert"), "Boolean literals should convert to Oracle numeric literals.");
        string oracleBooleanSql = (string)GetProperty(oracleBooleanPreview, "ConvertedSql");
        AssertContains(oracleBooleanSql, "SELECT 0 AS is_deleted, 1 AS is_active", "Converted Oracle SQL should use numeric boolean literals.");

        object mysqlBooleanPreview = BuildViewSqlPreview(
            "SELECT TRUE AS is_active FROM users WHERE deleted = FALSE",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlBooleanPreview, "CanConvert"), "Boolean literals should stay native for MySQL.");
        string mysqlBooleanSql = (string)GetProperty(mysqlBooleanPreview, "ConvertedSql");
        AssertContains(mysqlBooleanSql, "TRUE AS is_active", "Converted MySQL SQL should keep TRUE.");
        AssertContains(mysqlBooleanSql, "deleted = FALSE", "Converted MySQL SQL should keep FALSE.");

        object mssqlPgCastPreview = BuildViewSqlPreview(
            "SELECT amount::numeric(10,2) AS amount_value, created_at::timestamp AS created_at, 'amount::numeric' AS sample FROM orders",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlPgCastPreview, "CanConvert"), "PostgreSQL cast operators should convert to SQL Server CAST.");
        string mssqlPgCastSql = (string)GetProperty(mssqlPgCastPreview, "ConvertedSql");
        AssertContains(mssqlPgCastSql, "CAST(amount AS decimal(10,2))", "Converted SQL Server SQL should cast numeric precision.");
        AssertContains(mssqlPgCastSql, "CAST(created_at AS datetime2)", "Converted SQL Server SQL should cast timestamp.");
        AssertContains(mssqlPgCastSql, "'amount::numeric'", "Converted SQL Server SQL should preserve cast text in string literals.");

        object pgSqlServerConvertPreview = BuildViewSqlPreview(
            "SELECT CONVERT(int, user_id_text) AS user_id, CONVERT(decimal(10,2), amount_text) AS amount FROM orders",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgSqlServerConvertPreview, "CanConvert"), "SQL Server CONVERT casts should convert to PostgreSQL.");
        string pgSqlServerConvertSql = (string)GetProperty(pgSqlServerConvertPreview, "ConvertedSql");
        AssertContains(pgSqlServerConvertSql, "CAST(user_id_text AS INTEGER)", "Converted PostgreSQL SQL should cast SQL Server int CONVERT.");
        AssertContains(pgSqlServerConvertSql, "CAST(amount_text AS decimal(10,2))", "Converted PostgreSQL SQL should cast SQL Server decimal CONVERT.");
        AssertNotContains(pgSqlServerConvertSql, "CONVERT(int", "Converted PostgreSQL SQL should remove SQL Server int CONVERT.");

        object pgNestedSqlServerConvertPreview = BuildViewSqlPreview(
            "SELECT CONVERT(int, REPLACE(user_id_text, '-', '')) AS user_id, 'CONVERT(int, REPLACE(user_id_text, ''-'', ''''))' AS literal_note FROM orders",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgNestedSqlServerConvertPreview, "CanConvert"), "Nested SQL Server CONVERT casts should convert while preserving literals.");
        string pgNestedSqlServerConvertSql = (string)GetProperty(pgNestedSqlServerConvertPreview, "ConvertedSql");
        AssertContains(pgNestedSqlServerConvertSql, "CAST(REPLACE(user_id_text, '-', '') AS INTEGER) AS user_id", "Converted PostgreSQL SQL should cast nested SQL Server CONVERT expression.");
        AssertContains(pgNestedSqlServerConvertSql, "'CONVERT(int, REPLACE(user_id_text, ''-'', ''''))' AS literal_note", "Converted PostgreSQL SQL should preserve CONVERT text inside string literals.");

        object oracleSqlServerConvertPreview = BuildViewSqlPreview(
            "SELECT CONVERT(bigint, user_id_text) AS user_id, CONVERT(bit, enabled_text) AS enabled FROM users",
            "mssql",
            "oracle");
        Assert((bool)GetProperty(oracleSqlServerConvertPreview, "CanConvert"), "SQL Server CONVERT casts should convert to Oracle.");
        string oracleSqlServerConvertSql = (string)GetProperty(oracleSqlServerConvertPreview, "ConvertedSql");
        AssertContains(oracleSqlServerConvertSql, "CAST(user_id_text AS NUMBER(19))", "Converted Oracle SQL should map bigint CONVERT to NUMBER(19).");
        AssertContains(oracleSqlServerConvertSql, "CAST(enabled_text AS NUMBER(1))", "Converted Oracle SQL should map bit CONVERT to NUMBER(1).");

        object mysqlPgCastPreview = BuildViewSqlPreview(
            "SELECT user_id::bigint AS user_id, display_name::text AS display_name FROM users",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlPgCastPreview, "CanConvert"), "PostgreSQL cast operators should convert to MySQL CAST.");
        string mysqlPgCastSql = (string)GetProperty(mysqlPgCastPreview, "ConvertedSql");
        AssertContains(mysqlPgCastSql, "CAST(user_id AS SIGNED)", "Converted MySQL SQL should cast bigint to SIGNED.");
        AssertContains(mysqlPgCastSql, "CAST(display_name AS CHAR)", "Converted MySQL SQL should cast text to CHAR.");

        object oraclePgCastPreview = BuildViewSqlPreview(
            "SELECT enabled::boolean AS enabled_flag, started_at::timestamptz AS started_at FROM flags",
            "postgresql",
            "oracle");
        Assert((bool)GetProperty(oraclePgCastPreview, "CanConvert"), "PostgreSQL cast operators should convert to Oracle CAST.");
        string oraclePgCastSql = (string)GetProperty(oraclePgCastPreview, "ConvertedSql");
        AssertContains(oraclePgCastSql, "CAST(enabled AS NUMBER(1))", "Converted Oracle SQL should cast boolean to NUMBER(1).");
        AssertContains(oraclePgCastSql, "CAST(started_at AS TIMESTAMP)", "Converted Oracle SQL should cast timestamp to TIMESTAMP.");

        object mysqlTryCastPreview = BuildViewSqlPreview(
            "SELECT TRY_CAST(amount_text AS decimal(10,2)) AS amount_value FROM orders",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlTryCastPreview, "CanConvert"), "SQL Server TRY_CAST should convert to MySQL.");
        string mysqlTryCastSql = (string)GetProperty(mysqlTryCastPreview, "ConvertedSql");
        AssertContains(mysqlTryCastSql, "CAST(amount_text AS decimal(10,2))", "Converted MySQL SQL should use CAST for TRY_CAST.");
        AssertNotContains(mysqlTryCastSql, "TRY_CAST", "Converted MySQL SQL should remove TRY_CAST.");

        object pgNestedTryCastPreview = BuildViewSqlPreview(
            "SELECT TRY_CAST(REPLACE(user_id_text, '-', '') AS int) AS user_id, 'TRY_CAST(REPLACE(user_id_text, ''-'', '''') AS int)' AS literal_note FROM orders",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgNestedTryCastPreview, "CanConvert"), "Nested SQL Server TRY_CAST should convert while preserving literals.");
        string pgNestedTryCastSql = (string)GetProperty(pgNestedTryCastPreview, "ConvertedSql");
        AssertContains(pgNestedTryCastSql, "CAST(REPLACE(user_id_text, '-', '') AS INTEGER) AS user_id", "Converted PostgreSQL SQL should cast nested TRY_CAST expression.");
        AssertContains(pgNestedTryCastSql, "'TRY_CAST(REPLACE(user_id_text, ''-'', '''') AS int)' AS literal_note", "Converted PostgreSQL SQL should preserve TRY_CAST text inside string literals.");

        object pgTryConvertDatePreview = BuildViewSqlPreview(
            "SELECT TRY_CONVERT(date, order_date_text, 23) AS order_date FROM orders",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgTryConvertDatePreview, "CanConvert"), "SQL Server TRY_CONVERT date should convert to PostgreSQL.");
        string pgTryConvertDateSql = (string)GetProperty(pgTryConvertDatePreview, "ConvertedSql");
        AssertContains(pgTryConvertDateSql, "TO_DATE(order_date_text, 'YYYY-MM-DD')", "Converted PostgreSQL SQL should parse date style 23.");
        AssertNotContains(pgTryConvertDateSql, "TRY_CONVERT", "Converted PostgreSQL SQL should remove TRY_CONVERT.");

        object pgNestedTryConvertPreview = BuildViewSqlPreview(
            "SELECT TRY_CONVERT(int, REPLACE(user_id_text, '-', '')) AS user_id, 'TRY_CONVERT(int, REPLACE(user_id_text, ''-'', ''''))' AS literal_note FROM orders",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgNestedTryConvertPreview, "CanConvert"), "Nested SQL Server TRY_CONVERT should convert while preserving literals.");
        string pgNestedTryConvertSql = (string)GetProperty(pgNestedTryConvertPreview, "ConvertedSql");
        AssertContains(pgNestedTryConvertSql, "CAST(REPLACE(user_id_text, '-', '') AS INTEGER) AS user_id", "Converted PostgreSQL SQL should cast nested TRY_CONVERT expression.");
        AssertContains(pgNestedTryConvertSql, "'TRY_CONVERT(int, REPLACE(user_id_text, ''-'', ''''))' AS literal_note", "Converted PostgreSQL SQL should preserve TRY_CONVERT text inside string literals.");

        object oracleTryConvertDateTimePreview = BuildViewSqlPreview(
            "SELECT TRY_CONVERT(datetime, created_text, 120) AS created_at FROM orders",
            "mssql",
            "oracle");
        Assert((bool)GetProperty(oracleTryConvertDateTimePreview, "CanConvert"), "SQL Server TRY_CONVERT datetime should convert to Oracle.");
        string oracleTryConvertDateTimeSql = (string)GetProperty(oracleTryConvertDateTimePreview, "ConvertedSql");
        AssertContains(oracleTryConvertDateTimeSql, "TO_DATE(created_text, 'YYYY-MM-DD HH24:MI:SS')", "Converted Oracle SQL should parse datetime style 120.");

        object sqliteTryCastPreview = BuildViewSqlPreview(
            "SELECT TRY_CAST(user_id_text AS int) AS user_id FROM users",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteTryCastPreview, "CanConvert"), "SQL Server TRY_CAST int should convert to SQLite.");
        string sqliteTryCastSql = (string)GetProperty(sqliteTryCastPreview, "ConvertedSql");
        AssertContains(sqliteTryCastSql, "CAST(user_id_text AS INTEGER)", "Converted SQLite SQL should use CAST INTEGER.");

        object pgCastPreview = BuildViewSqlPreview(
            "SELECT amount::numeric(10,2) AS amount_value FROM orders",
            "postgresql",
            "postgresql");
        Assert((bool)GetProperty(pgCastPreview, "CanConvert"), "PostgreSQL target should keep native PostgreSQL cast operators.");
        string pgCastSql = (string)GetProperty(pgCastPreview, "ConvertedSql");
        AssertContains(pgCastSql, "amount::numeric(10,2)", "Converted PostgreSQL SQL should keep native cast syntax.");

        object mssqlNullsLastPreview = BuildViewSqlPreview(
            "SELECT id, due_at FROM tasks ORDER BY due_at DESC NULLS LAST",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlNullsLastPreview, "CanConvert"), "PostgreSQL NULLS LAST should convert to SQL Server ORDER BY fallback.");
        string mssqlNullsLastSql = (string)GetProperty(mssqlNullsLastPreview, "ConvertedSql");
        AssertContains(mssqlNullsLastSql, "ORDER BY CASE WHEN due_at IS NULL THEN 1 ELSE 0 END, due_at DESC", "Converted SQL Server SQL should emulate NULLS LAST.");
        AssertNotContains(mssqlNullsLastSql, "NULLS LAST", "Converted SQL Server SQL should remove NULLS LAST.");

        object mysqlNullsFirstPreview = BuildViewSqlPreview(
            "SELECT id, priority FROM tasks ORDER BY priority ASC NULLS FIRST",
            "oracle",
            "mysql");
        Assert((bool)GetProperty(mysqlNullsFirstPreview, "CanConvert"), "Oracle NULLS FIRST should convert to MySQL ORDER BY fallback.");
        string mysqlNullsFirstSql = (string)GetProperty(mysqlNullsFirstPreview, "ConvertedSql");
        AssertContains(mysqlNullsFirstSql, "ORDER BY CASE WHEN priority IS NULL THEN 0 ELSE 1 END, priority ASC", "Converted MySQL SQL should emulate NULLS FIRST.");
        AssertNotContains(mysqlNullsFirstSql, "NULLS FIRST", "Converted MySQL SQL should remove NULLS FIRST.");

        object oracleNullsLastPreview = BuildViewSqlPreview(
            "SELECT id, due_at FROM tasks ORDER BY due_at DESC NULLS LAST",
            "postgresql",
            "oracle");
        Assert((bool)GetProperty(oracleNullsLastPreview, "CanConvert"), "Oracle target should keep native NULLS LAST.");
        string oracleNullsLastSql = (string)GetProperty(oracleNullsLastPreview, "ConvertedSql");
        AssertContains(oracleNullsLastSql, "due_at DESC NULLS LAST", "Converted Oracle SQL should keep NULLS LAST.");

        object pgPowPreview = BuildViewSqlPreview(
            "SELECT POW(score, 2) AS score_squared FROM exams",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgPowPreview, "CanConvert"), "MySQL POW should convert to PostgreSQL.");
        string pgPowSql = (string)GetProperty(pgPowPreview, "ConvertedSql");
        AssertContains(pgPowSql, "POWER(score, 2)", "Converted PostgreSQL SQL should use POWER.");

        object pgPowLiteralPreview = BuildViewSqlPreview(
            "SELECT POW(score, 2) AS score_squared, 'POW(note)' AS literal_note FROM exams",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgPowLiteralPreview, "CanConvert"), "MySQL POW should convert while preserving literals.");
        string pgPowLiteralSql = (string)GetProperty(pgPowLiteralPreview, "ConvertedSql");
        AssertContains(pgPowLiteralSql, "POWER(score, 2) AS score_squared", "Converted PostgreSQL SQL should convert POW function only.");
        AssertContains(pgPowLiteralSql, "'POW(note)' AS literal_note", "Converted PostgreSQL SQL should preserve POW text inside string literals.");

        object mssqlPowPreview = BuildViewSqlPreview(
            "SELECT POW(score, 2) AS score_squared FROM exams",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlPowPreview, "CanConvert"), "MySQL POW should convert to SQL Server.");
        string mssqlPowSql = (string)GetProperty(mssqlPowPreview, "ConvertedSql");
        AssertContains(mssqlPowSql, "POWER(score, 2)", "Converted SQL Server SQL should use POWER.");

        object pgRandPreview = BuildViewSqlPreview(
            "SELECT RAND() AS sample_value FROM metrics",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgRandPreview, "CanConvert"), "MySQL RAND should convert to PostgreSQL.");
        string pgRandSql = (string)GetProperty(pgRandPreview, "ConvertedSql");
        AssertContains(pgRandSql, "RANDOM()", "Converted PostgreSQL SQL should use RANDOM.");

        object pgRandLiteralPreview = BuildViewSqlPreview(
            "SELECT RAND() AS sample_value, 'RAND()' AS literal_note FROM metrics",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgRandLiteralPreview, "CanConvert"), "MySQL RAND should convert while preserving literals.");
        string pgRandLiteralSql = (string)GetProperty(pgRandLiteralPreview, "ConvertedSql");
        AssertContains(pgRandLiteralSql, "RANDOM() AS sample_value", "Converted PostgreSQL SQL should convert RAND function only.");
        AssertContains(pgRandLiteralSql, "'RAND()' AS literal_note", "Converted PostgreSQL SQL should preserve RAND text inside string literals.");

        object mssqlRandomPreview = BuildViewSqlPreview(
            "SELECT RANDOM() AS sample_value FROM metrics",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlRandomPreview, "CanConvert"), "PostgreSQL RANDOM should convert to SQL Server.");
        string mssqlRandomSql = (string)GetProperty(mssqlRandomPreview, "ConvertedSql");
        AssertContains(mssqlRandomSql, "RAND()", "Converted SQL Server SQL should use RAND.");

        object sqliteOracleRandomPreview = BuildViewSqlPreview(
            "SELECT DBMS_RANDOM.VALUE AS sample_value FROM metrics",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteOracleRandomPreview, "CanConvert"), "Oracle DBMS_RANDOM.VALUE should convert to SQLite.");
        string sqliteOracleRandomSql = (string)GetProperty(sqliteOracleRandomPreview, "ConvertedSql");
        AssertContains(sqliteOracleRandomSql, "(RANDOM() + 9223372036854775808.0) / 18446744073709551616.0", "Converted SQLite SQL should normalize RANDOM to 0-1 range.");

        object mysqlNewIdPreview = BuildViewSqlPreview(
            "SELECT NEWID() AS row_guid FROM metrics",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlNewIdPreview, "CanConvert"), "SQL Server NEWID should convert to MySQL.");
        string mysqlNewIdSql = (string)GetProperty(mysqlNewIdPreview, "ConvertedSql");
        AssertContains(mysqlNewIdSql, "UUID()", "Converted MySQL SQL should use UUID for NEWID.");
        AssertNotContains(mysqlNewIdSql, "NEWID", "Converted MySQL SQL should remove SQL Server NEWID.");

        object mssqlUuidPreview = BuildViewSqlPreview(
            "SELECT UUID() AS row_guid FROM metrics",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlUuidPreview, "CanConvert"), "MySQL UUID should convert to SQL Server.");
        string mssqlUuidSql = (string)GetProperty(mssqlUuidPreview, "ConvertedSql");
        AssertContains(mssqlUuidSql, "NEWID()", "Converted SQL Server SQL should use NEWID for UUID.");
        AssertNotContains(mssqlUuidSql, "UUID()", "Converted SQL Server SQL should remove MySQL UUID.");

        object sqliteSysGuidPreview = BuildViewSqlPreview(
            "SELECT SYS_GUID() AS row_guid FROM metrics",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqliteSysGuidPreview, "CanConvert"), "Oracle SYS_GUID should convert to SQLite.");
        string sqliteSysGuidSql = (string)GetProperty(sqliteSysGuidPreview, "ConvertedSql");
        AssertContains(sqliteSysGuidSql, "lower(hex(randomblob(4))", "Converted SQLite SQL should build a random UUID string.");
        AssertNotContains(sqliteSysGuidSql, "SYS_GUID", "Converted SQLite SQL should remove Oracle SYS_GUID.");

        object oracleConcatPreview = BuildViewSqlPreview(
            "SELECT CONCAT(first_name, ' ', last_name) AS full_name FROM users",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleConcatPreview, "CanConvert"), "MySQL CONCAT should convert to Oracle.");
        string oracleConcatSql = (string)GetProperty(oracleConcatPreview, "ConvertedSql");
        AssertContains(oracleConcatSql, "first_name || ' ' || last_name", "Converted Oracle SQL should use concatenation operator.");

        object sqliteConcatPreview = BuildViewSqlPreview(
            "SELECT CONCAT(code, '-', name) AS label FROM items",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteConcatPreview, "CanConvert"), "MySQL CONCAT should convert to SQLite.");
        string sqliteConcatSql = (string)GetProperty(sqliteConcatPreview, "ConvertedSql");
        AssertContains(sqliteConcatSql, "code || '-' || name", "Converted SQLite SQL should use concatenation operator.");

        object oracleConcatWsPreview = BuildViewSqlPreview(
            "SELECT CONCAT_WS('-', country_code, area_code, phone_no) AS phone_key FROM contacts",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleConcatWsPreview, "CanConvert"), "MySQL CONCAT_WS should convert to Oracle.");
        string oracleConcatWsSql = (string)GetProperty(oracleConcatWsPreview, "ConvertedSql");
        AssertContains(oracleConcatWsSql, "country_code || '-' || area_code || '-' || phone_no", "Converted Oracle SQL should expand CONCAT_WS with separators.");
        AssertNotContains(oracleConcatWsSql, "CONCAT_WS", "Converted Oracle SQL should remove CONCAT_WS.");

        object sqliteConcatWsPreview = BuildViewSqlPreview(
            "SELECT CONCAT_WS('-', country_code, area_code, phone_no) AS phone_key FROM contacts",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteConcatWsPreview, "CanConvert"), "MySQL CONCAT_WS should convert to SQLite.");
        string sqliteConcatWsSql = (string)GetProperty(sqliteConcatWsPreview, "ConvertedSql");
        AssertContains(sqliteConcatWsSql, "country_code || '-' || area_code || '-' || phone_no", "Converted SQLite SQL should expand CONCAT_WS with separators.");

        object mysqlConcatOperatorPreview = BuildViewSqlPreview(
            "SELECT first_name || ' ' || last_name AS full_name FROM users",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlConcatOperatorPreview, "CanConvert"), "PostgreSQL concat operator should convert to MySQL.");
        string mysqlConcatOperatorSql = (string)GetProperty(mysqlConcatOperatorPreview, "ConvertedSql");
        AssertContains(mysqlConcatOperatorSql, "CONCAT(first_name, ' ', last_name)", "Converted MySQL SQL should use CONCAT for ||.");
        AssertNotContains(mysqlConcatOperatorSql, "||", "Converted MySQL SQL should remove concat operators.");

        object mssqlConcatOperatorPreview = BuildViewSqlPreview(
            "SELECT first_name || ' ' || last_name AS full_name FROM users",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlConcatOperatorPreview, "CanConvert"), "Oracle concat operator should convert to SQL Server.");
        string mssqlConcatOperatorSql = (string)GetProperty(mssqlConcatOperatorPreview, "ConvertedSql");
        AssertContains(mssqlConcatOperatorSql, "first_name + ' ' + last_name", "Converted SQL Server SQL should use + for ||.");
        AssertNotContains(mssqlConcatOperatorSql, "||", "Converted SQL Server SQL should remove concat operators.");

        object pgLengthPreview = BuildViewSqlPreview(
            "SELECT LEN(display_name) AS name_length FROM users",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgLengthPreview, "CanConvert"), "SQL Server LEN should convert to PostgreSQL.");
        string pgLengthSql = (string)GetProperty(pgLengthPreview, "ConvertedSql");
        AssertContains(pgLengthSql, "LENGTH(display_name)", "Converted PostgreSQL SQL should use LENGTH.");

        object pgDataLengthPreview = BuildViewSqlPreview(
            "SELECT DATALENGTH(binary_payload) AS payload_bytes FROM files",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgDataLengthPreview, "CanConvert"), "SQL Server DATALENGTH should convert to PostgreSQL.");
        string pgDataLengthSql = (string)GetProperty(pgDataLengthPreview, "ConvertedSql");
        AssertContains(pgDataLengthSql, "OCTET_LENGTH(binary_payload)", "Converted PostgreSQL SQL should preserve DATALENGTH byte-length semantics.");
        AssertNotContains(pgDataLengthSql, "DATALENGTH", "Converted PostgreSQL SQL should remove SQL Server DATALENGTH.");

        object sqliteDataLengthPreview = BuildViewSqlPreview(
            "SELECT DATALENGTH(binary_payload) AS payload_bytes FROM files",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteDataLengthPreview, "CanConvert"), "SQL Server DATALENGTH should convert to SQLite.");
        string sqliteDataLengthSql = (string)GetProperty(sqliteDataLengthPreview, "ConvertedSql");
        AssertContains(sqliteDataLengthSql, "length(CAST(binary_payload AS BLOB))", "Converted SQLite SQL should preserve DATALENGTH byte-length semantics.");

        object mssqlBitLengthPreview = BuildViewSqlPreview(
            "SELECT BIT_LENGTH(binary_payload) AS payload_bits FROM files",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlBitLengthPreview, "CanConvert"), "MySQL BIT_LENGTH should convert to SQL Server.");
        string mssqlBitLengthSql = (string)GetProperty(mssqlBitLengthPreview, "ConvertedSql");
        AssertContains(mssqlBitLengthSql, "(DATALENGTH(binary_payload) * 8)", "Converted SQL Server SQL should preserve BIT_LENGTH bit-count semantics.");
        AssertNotContains(mssqlBitLengthSql, "BIT_LENGTH", "Converted SQL Server SQL should remove MySQL BIT_LENGTH.");

        object sqliteBitLengthPreview = BuildViewSqlPreview(
            "SELECT BIT_LENGTH(binary_payload) AS payload_bits FROM files",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteBitLengthPreview, "CanConvert"), "MySQL BIT_LENGTH should convert to SQLite.");
        string sqliteBitLengthSql = (string)GetProperty(sqliteBitLengthPreview, "ConvertedSql");
        AssertContains(sqliteBitLengthSql, "(length(CAST(binary_payload AS BLOB)) * 8)", "Converted SQLite SQL should preserve BIT_LENGTH bit-count semantics.");

        object mssqlCharLengthPreview = BuildViewSqlPreview(
            "SELECT CHAR_LENGTH(display_name) AS name_length FROM users",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlCharLengthPreview, "CanConvert"), "MySQL CHAR_LENGTH should convert to SQL Server.");
        string mssqlCharLengthSql = (string)GetProperty(mssqlCharLengthPreview, "ConvertedSql");
        AssertContains(mssqlCharLengthSql, "LEN(display_name)", "Converted SQL Server SQL should use LEN for CHAR_LENGTH.");

        object sqliteCharacterLengthPreview = BuildViewSqlPreview(
            "SELECT CHARACTER_LENGTH(display_name) AS name_length FROM users",
            "postgresql",
            "sqlite");
        Assert((bool)GetProperty(sqliteCharacterLengthPreview, "CanConvert"), "PostgreSQL CHARACTER_LENGTH should convert to SQLite.");
        string sqliteCharacterLengthSql = (string)GetProperty(sqliteCharacterLengthPreview, "ConvertedSql");
        AssertContains(sqliteCharacterLengthSql, "LENGTH(display_name)", "Converted SQLite SQL should use LENGTH for CHARACTER_LENGTH.");

        object mssqlCaseAliasPreview = BuildViewSqlPreview(
            "SELECT UCASE(display_name) AS upper_name, LCASE(email) AS lower_email FROM users",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlCaseAliasPreview, "CanConvert"), "MySQL UCASE/LCASE aliases should convert to SQL Server.");
        string mssqlCaseAliasSql = (string)GetProperty(mssqlCaseAliasPreview, "ConvertedSql");
        AssertContains(mssqlCaseAliasSql, "UPPER(display_name)", "Converted SQL Server SQL should use UPPER for UCASE.");
        AssertContains(mssqlCaseAliasSql, "LOWER(email)", "Converted SQL Server SQL should use LOWER for LCASE.");
        AssertNotContains(mssqlCaseAliasSql, "UCASE", "Converted SQL Server SQL should remove UCASE.");
        AssertNotContains(mssqlCaseAliasSql, "LCASE", "Converted SQL Server SQL should remove LCASE.");

        object sqliteCaseAliasPreview = BuildViewSqlPreview(
            "SELECT UCASE(display_name) AS upper_name, LCASE(email) AS lower_email FROM users",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteCaseAliasPreview, "CanConvert"), "MySQL UCASE/LCASE aliases should convert to SQLite.");
        string sqliteCaseAliasSql = (string)GetProperty(sqliteCaseAliasPreview, "ConvertedSql");
        AssertContains(sqliteCaseAliasSql, "UPPER(display_name)", "Converted SQLite SQL should use UPPER for UCASE.");
        AssertContains(sqliteCaseAliasSql, "LOWER(email)", "Converted SQLite SQL should use LOWER for LCASE.");

        object pgNestedCaseLiteralPreview = BuildViewSqlPreview(
            "SELECT UCASE(TRIM(display_name)) AS upper_name, LCASE(TRIM(email)) AS lower_email, 'UCASE(TRIM(display_name))' AS literal_note FROM users",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgNestedCaseLiteralPreview, "CanConvert"), "Nested MySQL string case functions should convert while preserving literals.");
        string pgNestedCaseLiteralSql = (string)GetProperty(pgNestedCaseLiteralPreview, "ConvertedSql");
        AssertContains(pgNestedCaseLiteralSql, "UPPER(TRIM(display_name)) AS upper_name", "Converted PostgreSQL SQL should convert nested UCASE expression.");
        AssertContains(pgNestedCaseLiteralSql, "LOWER(TRIM(email)) AS lower_email", "Converted PostgreSQL SQL should convert nested LCASE expression.");
        AssertContains(pgNestedCaseLiteralSql, "'UCASE(TRIM(display_name))' AS literal_note", "Converted PostgreSQL SQL should preserve UCASE text inside string literals.");

        object pgTrimPreview = BuildViewSqlPreview(
            "SELECT LTRIM(RTRIM(display_name)) AS clean_name FROM users",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgTrimPreview, "CanConvert"), "SQL Server LTRIM/RTRIM should convert to PostgreSQL.");
        string pgTrimSql = (string)GetProperty(pgTrimPreview, "ConvertedSql");
        AssertContains(pgTrimSql, "TRIM(display_name)", "Converted PostgreSQL SQL should use TRIM.");

        object pgNestedTrimLiteralPreview = BuildViewSqlPreview(
            "SELECT LTRIM(RTRIM(REPLACE(display_name, '-', ''))) AS clean_name, 'LTRIM(RTRIM(REPLACE(display_name, ''-'', '''')))' AS literal_note FROM users",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgNestedTrimLiteralPreview, "CanConvert"), "Nested SQL Server LTRIM/RTRIM should convert while preserving literals.");
        string pgNestedTrimLiteralSql = (string)GetProperty(pgNestedTrimLiteralPreview, "ConvertedSql");
        AssertContains(pgNestedTrimLiteralSql, "TRIM(REPLACE(display_name, '-', '')) AS clean_name", "Converted PostgreSQL SQL should convert nested LTRIM/RTRIM expression.");
        AssertContains(pgNestedTrimLiteralSql, "'LTRIM(RTRIM(REPLACE(display_name, ''-'', '''')))' AS literal_note", "Converted PostgreSQL SQL should preserve LTRIM/RTRIM text inside string literals.");

        object mysqlTrimPreview = BuildViewSqlPreview(
            "SELECT RTRIM(LTRIM(display_name)) AS clean_name FROM users",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlTrimPreview, "CanConvert"), "SQL Server RTRIM/LTRIM should convert to MySQL.");
        string mysqlTrimSql = (string)GetProperty(mysqlTrimPreview, "ConvertedSql");
        AssertContains(mysqlTrimSql, "TRIM(display_name)", "Converted MySQL SQL should use TRIM.");

        object mssqlTrimPreview = BuildViewSqlPreview(
            "SELECT TRIM(display_name) AS clean_name FROM users",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlTrimPreview, "CanConvert"), "PostgreSQL TRIM should convert to SQL Server.");
        string mssqlTrimSql = (string)GetProperty(mssqlTrimPreview, "ConvertedSql");
        AssertContains(mssqlTrimSql, "LTRIM(RTRIM(display_name))", "Converted SQL Server SQL should use LTRIM/RTRIM.");

        object mssqlNestedTrimLiteralPreview = BuildViewSqlPreview(
            "SELECT TRIM(REPLACE(display_name, '-', '')) AS clean_name, 'TRIM(REPLACE(display_name, ''-'', ''''))' AS literal_note FROM users",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlNestedTrimLiteralPreview, "CanConvert"), "Nested PostgreSQL TRIM should convert while preserving literals.");
        string mssqlNestedTrimLiteralSql = (string)GetProperty(mssqlNestedTrimLiteralPreview, "ConvertedSql");
        AssertContains(mssqlNestedTrimLiteralSql, "LTRIM(RTRIM(REPLACE(display_name, '-', ''))) AS clean_name", "Converted SQL Server SQL should convert nested TRIM expression.");
        AssertContains(mssqlNestedTrimLiteralSql, "'TRIM(REPLACE(display_name, ''-'', ''''))' AS literal_note", "Converted SQL Server SQL should preserve TRIM text inside string literals.");

        object mssqlLengthPreview = BuildViewSqlPreview(
            "SELECT LENGTH(display_name) AS name_length FROM users",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlLengthPreview, "CanConvert"), "PostgreSQL LENGTH should convert to SQL Server.");
        string mssqlLengthSql = (string)GetProperty(mssqlLengthPreview, "ConvertedSql");
        AssertContains(mssqlLengthSql, "LEN(display_name)", "Converted SQL Server SQL should use LEN.");

        object sqliteSubstringPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING(code, 1, 3) AS prefix FROM items",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteSubstringPreview, "CanConvert"), "SQL Server SUBSTRING should convert to SQLite.");
        string sqliteSubstringSql = (string)GetProperty(sqliteSubstringPreview, "ConvertedSql");
        AssertContains(sqliteSubstringSql, "SUBSTR(code, 1, 3)", "Converted SQLite SQL should use SUBSTR.");

        object mssqlSubstringPreview = BuildViewSqlPreview(
            "SELECT SUBSTR(code, 1, 3) AS prefix FROM items",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlSubstringPreview, "CanConvert"), "Oracle SUBSTR should convert to SQL Server.");
        string mssqlSubstringSql = (string)GetProperty(mssqlSubstringPreview, "ConvertedSql");
        AssertContains(mssqlSubstringSql, "SUBSTRING(code, 1, 3)", "Converted SQL Server SQL should use SUBSTRING.");

        object mssqlNestedSubstrLiteralPreview = BuildViewSqlPreview(
            "SELECT SUBSTR(REPLACE(code, '-', ''), 1, 3) AS prefix, 'SUBSTR(REPLACE(code, ''-'', ''''), 1, 3)' AS literal_note FROM items",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlNestedSubstrLiteralPreview, "CanConvert"), "Nested Oracle SUBSTR should convert while preserving literals.");
        string mssqlNestedSubstrLiteralSql = (string)GetProperty(mssqlNestedSubstrLiteralPreview, "ConvertedSql");
        AssertContains(mssqlNestedSubstrLiteralSql, "SUBSTRING(REPLACE(code, '-', ''), 1, 3) AS prefix", "Converted SQL Server SQL should convert nested SUBSTR expression.");
        AssertContains(mssqlNestedSubstrLiteralSql, "'SUBSTR(REPLACE(code, ''-'', ''''), 1, 3)' AS literal_note", "Converted SQL Server SQL should preserve SUBSTR text inside string literals.");

        object sqliteNestedSubstringLiteralPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING(REPLACE(code, '-', ''), 1, 3) AS prefix, 'SUBSTRING(REPLACE(code, ''-'', ''''), 1, 3)' AS literal_note FROM items",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteNestedSubstringLiteralPreview, "CanConvert"), "Nested SQL Server SUBSTRING should convert while preserving literals.");
        string sqliteNestedSubstringLiteralSql = (string)GetProperty(sqliteNestedSubstringLiteralPreview, "ConvertedSql");
        AssertContains(sqliteNestedSubstringLiteralSql, "SUBSTR(REPLACE(code, '-', ''), 1, 3) AS prefix", "Converted SQLite SQL should convert nested SUBSTRING expression.");
        AssertContains(sqliteNestedSubstringLiteralSql, "'SUBSTRING(REPLACE(code, ''-'', ''''), 1, 3)' AS literal_note", "Converted SQLite SQL should preserve SUBSTRING text inside string literals.");

        object mysqlStandardSubstringPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING(code FROM 2 FOR 4) AS middle_code FROM items",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlStandardSubstringPreview, "CanConvert"), "Standard SUBSTRING syntax should convert to MySQL.");
        string mysqlStandardSubstringSql = (string)GetProperty(mysqlStandardSubstringPreview, "ConvertedSql");
        AssertContains(mysqlStandardSubstringSql, "SUBSTRING(code, 2, 4)", "Converted MySQL SQL should use comma SUBSTRING arguments.");

        object oracleStandardSubstringPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING(code FROM 2 FOR 4) AS middle_code FROM items",
            "postgresql",
            "oracle");
        Assert((bool)GetProperty(oracleStandardSubstringPreview, "CanConvert"), "Standard SUBSTRING syntax should convert to Oracle.");
        string oracleStandardSubstringSql = (string)GetProperty(oracleStandardSubstringPreview, "ConvertedSql");
        AssertContains(oracleStandardSubstringSql, "SUBSTR(code, 2, 4)", "Converted Oracle SQL should use SUBSTR arguments.");

        object mssqlStandardSubstringPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING(code FROM 2 FOR 4) AS middle_code FROM items",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlStandardSubstringPreview, "CanConvert"), "Standard SUBSTRING syntax should convert to SQL Server.");
        string mssqlStandardSubstringSql = (string)GetProperty(mssqlStandardSubstringPreview, "ConvertedSql");
        AssertContains(mssqlStandardSubstringSql, "SUBSTRING(code, 2, 4)", "Converted SQL Server SQL should use comma SUBSTRING arguments.");

        object oracleLeftPreview = BuildViewSqlPreview(
            "SELECT LEFT(code, 3) AS prefix FROM items",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleLeftPreview, "CanConvert"), "MySQL LEFT should convert to Oracle.");
        string oracleLeftSql = (string)GetProperty(oracleLeftPreview, "ConvertedSql");
        AssertContains(oracleLeftSql, "SUBSTR(code, 1, 3)", "Converted Oracle SQL should use SUBSTR for LEFT.");

        object sqliteRightPreview = BuildViewSqlPreview(
            "SELECT RIGHT(code, 2) AS suffix FROM items",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteRightPreview, "CanConvert"), "SQL Server RIGHT should convert to SQLite.");
        string sqliteRightSql = (string)GetProperty(sqliteRightPreview, "ConvertedSql");
        AssertContains(sqliteRightSql, "SUBSTR(code, -2)", "Converted SQLite SQL should use negative SUBSTR for RIGHT.");

        object oracleNestedEdgeSubstringLiteralPreview = BuildViewSqlPreview(
            "SELECT LEFT(REPLACE(code, '-', ''), 3) AS prefix, RIGHT(REPLACE(code, '-', ''), 2) AS suffix, 'LEFT(REPLACE(code, ''-'', ''''), 3)' AS literal_note FROM items",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleNestedEdgeSubstringLiteralPreview, "CanConvert"), "Nested LEFT/RIGHT should convert while preserving literals.");
        string oracleNestedEdgeSubstringLiteralSql = (string)GetProperty(oracleNestedEdgeSubstringLiteralPreview, "ConvertedSql");
        AssertContains(oracleNestedEdgeSubstringLiteralSql, "SUBSTR(REPLACE(code, '-', ''), 1, 3) AS prefix", "Converted Oracle SQL should convert nested LEFT expression.");
        AssertContains(oracleNestedEdgeSubstringLiteralSql, "SUBSTR(REPLACE(code, '-', ''), -2) AS suffix", "Converted Oracle SQL should convert nested RIGHT expression.");
        AssertContains(oracleNestedEdgeSubstringLiteralSql, "'LEFT(REPLACE(code, ''-'', ''''), 3)' AS literal_note", "Converted Oracle SQL should preserve LEFT text inside string literals.");

        object mssqlPadPreview = BuildViewSqlPreview(
            "SELECT LPAD(account_no, 10, '0') AS padded_account, RPAD(code, 8, ' ') AS padded_code FROM accounts",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlPadPreview, "CanConvert"), "MySQL LPAD/RPAD should convert to SQL Server.");
        string mssqlPadSql = (string)GetProperty(mssqlPadPreview, "ConvertedSql");
        AssertContains(mssqlPadSql, "RIGHT(REPLICATE('0', 10) + CAST(account_no AS varchar(max)), 10)", "Converted SQL Server SQL should emulate LPAD.");
        AssertContains(mssqlPadSql, "LEFT(CAST(code AS varchar(max)) + REPLICATE(' ', 8), 8)", "Converted SQL Server SQL should emulate RPAD.");
        AssertNotContains(mssqlPadSql, "LPAD", "Converted SQL Server SQL should remove LPAD.");
        AssertNotContains(mssqlPadSql, "RPAD", "Converted SQL Server SQL should remove RPAD.");

        object sqlitePadPreview = BuildViewSqlPreview(
            "SELECT LPAD(account_no, 10, '0') AS padded_account, RPAD(code, 8, ' ') AS padded_code FROM accounts",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqlitePadPreview, "CanConvert"), "MySQL LPAD/RPAD should convert to SQLite.");
        string sqlitePadSql = (string)GetProperty(sqlitePadPreview, "ConvertedSql");
        AssertContains(sqlitePadSql, "SUBSTR(REPLACE(HEX(ZEROBLOB(10)), '00', '0') || CAST(account_no AS TEXT), -10, 10)", "Converted SQLite SQL should emulate LPAD.");
        AssertContains(sqlitePadSql, "SUBSTR(CAST(code AS TEXT) || REPLACE(HEX(ZEROBLOB(8)), '00', ' '), 1, 8)", "Converted SQLite SQL should emulate RPAD.");
        AssertNotContains(sqlitePadSql, "LPAD", "Converted SQLite SQL should remove LPAD.");
        AssertNotContains(sqlitePadSql, "RPAD", "Converted SQLite SQL should remove RPAD.");

        object mssqlNestedPadLiteralPreview = BuildViewSqlPreview(
            "SELECT LPAD(REPLACE(account_no, '-', ''), 10, '0') AS padded_account, RPAD(REPLACE(code, '-', ''), 8, ' ') AS padded_code, 'LPAD(REPLACE(account_no, ''-'', ''''), 10, ''0'')' AS literal_note FROM accounts",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlNestedPadLiteralPreview, "CanConvert"), "Nested MySQL LPAD/RPAD should convert while preserving literals.");
        string mssqlNestedPadLiteralSql = (string)GetProperty(mssqlNestedPadLiteralPreview, "ConvertedSql");
        AssertContains(mssqlNestedPadLiteralSql, "RIGHT(REPLICATE('0', 10) + CAST(REPLACE(account_no, '-', '') AS varchar(max)), 10) AS padded_account", "Converted SQL Server SQL should convert nested LPAD expression.");
        AssertContains(mssqlNestedPadLiteralSql, "LEFT(CAST(REPLACE(code, '-', '') AS varchar(max)) + REPLICATE(' ', 8), 8) AS padded_code", "Converted SQL Server SQL should convert nested RPAD expression.");
        AssertContains(mssqlNestedPadLiteralSql, "'LPAD(REPLACE(account_no, ''-'', ''''), 10, ''0'')' AS literal_note", "Converted SQL Server SQL should preserve LPAD text inside string literals.");

        object mssqlRepeatPreview = BuildViewSqlPreview(
            "SELECT REPEAT('0', 4) || code AS padded_code FROM items",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlRepeatPreview, "CanConvert"), "MySQL REPEAT should convert to SQL Server.");
        string mssqlRepeatSql = (string)GetProperty(mssqlRepeatPreview, "ConvertedSql");
        AssertContains(mssqlRepeatSql, "REPLICATE('0', 4)", "Converted SQL Server SQL should use REPLICATE for REPEAT.");
        AssertNotContains(mssqlRepeatSql, "REPEAT", "Converted SQL Server SQL should remove REPEAT.");

        object sqliteReplicatePreview = BuildViewSqlPreview(
            "SELECT REPLICATE('*', 3) + mask AS masked_code FROM items",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteReplicatePreview, "CanConvert"), "SQL Server REPLICATE should convert to SQLite.");
        string sqliteReplicateSql = (string)GetProperty(sqliteReplicatePreview, "ConvertedSql");
        AssertContains(sqliteReplicateSql, "REPLACE(HEX(ZEROBLOB(3)), '00', '*')", "Converted SQLite SQL should emulate REPLICATE.");
        AssertNotContains(sqliteReplicateSql, "REPLICATE", "Converted SQLite SQL should remove REPLICATE.");

        object pgSpacePreview = BuildViewSqlPreview(
            "SELECT SPACE(3) AS indent_text FROM items",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgSpacePreview, "CanConvert"), "SQL Server SPACE should convert to PostgreSQL.");
        string pgSpaceSql = (string)GetProperty(pgSpacePreview, "ConvertedSql");
        AssertContains(pgSpaceSql, "REPEAT(' ', 3)", "Converted PostgreSQL SQL should use REPEAT for SPACE.");
        AssertNotContains(pgSpaceSql, "SPACE", "Converted PostgreSQL SQL should remove SPACE.");

        object sqliteSpacePreview = BuildViewSqlPreview(
            "SELECT SPACE(2) AS indent_text FROM items",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteSpacePreview, "CanConvert"), "MySQL SPACE should convert to SQLite.");
        string sqliteSpaceSql = (string)GetProperty(sqliteSpacePreview, "ConvertedSql");
        AssertContains(sqliteSpaceSql, "REPLACE(HEX(ZEROBLOB(2)), '00', ' ')", "Converted SQLite SQL should emulate SPACE.");
        AssertNotContains(sqliteSpaceSql, "SPACE", "Converted SQLite SQL should remove SPACE.");

        object mssqlChrPreview = BuildViewSqlPreview(
            "SELECT 'line1' || CHR(10) || 'line2' AS message_text FROM messages",
            "oracle",
            "mssql");
        Assert((bool)GetProperty(mssqlChrPreview, "CanConvert"), "Oracle CHR should convert to SQL Server.");
        string mssqlChrSql = (string)GetProperty(mssqlChrPreview, "ConvertedSql");
        AssertContains(mssqlChrSql, "CHAR(10)", "Converted SQL Server SQL should use CHAR for CHR.");
        AssertNotContains(mssqlChrSql, "CHR(10)", "Converted SQL Server SQL should remove CHR.");

        object pgCharCodePreview = BuildViewSqlPreview(
            "SELECT CHAR(65) AS initial_letter FROM users",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgCharCodePreview, "CanConvert"), "MySQL CHAR code function should convert to PostgreSQL.");
        string pgCharCodeSql = (string)GetProperty(pgCharCodePreview, "ConvertedSql");
        AssertContains(pgCharCodeSql, "CHR(65)", "Converted PostgreSQL SQL should use CHR for CHAR code function.");

        object pgCastCharPreview = BuildViewSqlPreview(
            "SELECT CAST(code AS CHAR(10)) AS text_code FROM items",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgCastCharPreview, "CanConvert"), "CHAR type in CAST should not be treated as a character-code function.");
        string pgCastCharSql = (string)GetProperty(pgCastCharPreview, "ConvertedSql");
        AssertContains(pgCastCharSql, "CAST(code AS CHAR(10))", "Converted PostgreSQL SQL should preserve CHAR type casts.");
        AssertNotContains(pgCastCharSql, "AS CHR(10)", "Converted PostgreSQL SQL should not rewrite CHAR type to CHR.");

        object sqliteAsciiPreview = BuildViewSqlPreview(
            "SELECT ASCII(initial_letter) AS initial_code FROM users",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteAsciiPreview, "CanConvert"), "MySQL ASCII should convert to SQLite.");
        string sqliteAsciiSql = (string)GetProperty(sqliteAsciiPreview, "ConvertedSql");
        AssertContains(sqliteAsciiSql, "unicode(initial_letter)", "Converted SQLite SQL should use unicode for ASCII.");
        AssertNotContains(sqliteAsciiSql, "ASCII", "Converted SQLite SQL should remove ASCII.");

        object pgUnicodePreview = BuildViewSqlPreview(
            "SELECT UNICODE(initial_letter) AS initial_code FROM users",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgUnicodePreview, "CanConvert"), "SQL Server UNICODE should convert to PostgreSQL.");
        string pgUnicodeSql = (string)GetProperty(pgUnicodePreview, "ConvertedSql");
        AssertContains(pgUnicodeSql, "ASCII(initial_letter)", "Converted PostgreSQL SQL should use ASCII for UNICODE.");
        AssertNotContains(pgUnicodeSql, "UNICODE", "Converted PostgreSQL SQL should remove UNICODE.");

        object pgNcharPreview = BuildViewSqlPreview(
            "SELECT NCHAR(9731) AS snow_text FROM users",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgNcharPreview, "CanConvert"), "SQL Server NCHAR code function should convert to PostgreSQL.");
        string pgNcharSql = (string)GetProperty(pgNcharPreview, "ConvertedSql");
        AssertContains(pgNcharSql, "CHR(9731)", "Converted PostgreSQL SQL should use CHR for NCHAR code function.");
        AssertNotContains(pgNcharSql, "NCHAR", "Converted PostgreSQL SQL should remove NCHAR code function.");

        object pgCastNcharPreview = BuildViewSqlPreview(
            "SELECT CAST(code AS NCHAR(10)) AS text_code FROM items",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgCastNcharPreview, "CanConvert"), "NCHAR type in CAST should not be treated as a character-code function.");
        string pgCastNcharSql = (string)GetProperty(pgCastNcharPreview, "ConvertedSql");
        AssertContains(pgCastNcharSql, "CAST(code AS TEXT)", "Converted PostgreSQL SQL should map SQL Server NCHAR type through cast conversion.");
        AssertNotContains(pgCastNcharSql, "AS CHR(10)", "Converted PostgreSQL SQL should not rewrite NCHAR type to CHR.");

        object pgMysqlIsNullPreview = BuildViewSqlPreview(
            "SELECT ISNULL(deleted_at) AS is_deleted_missing FROM users",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgMysqlIsNullPreview, "CanConvert"), "MySQL ISNULL predicate should convert to PostgreSQL.");
        string pgMysqlIsNullSql = (string)GetProperty(pgMysqlIsNullPreview, "ConvertedSql");
        AssertContains(pgMysqlIsNullSql, "deleted_at IS NULL", "Converted PostgreSQL SQL should rewrite one-argument MySQL ISNULL as IS NULL predicate.");
        AssertNotContains(pgMysqlIsNullSql, "COALESCE(deleted_at)", "Converted PostgreSQL SQL should not treat MySQL ISNULL predicate as COALESCE.");

        object pgSqlServerIsNullPreview = BuildViewSqlPreview(
            "SELECT ISNULL(display_name, '匿名') AS display_name FROM users",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgSqlServerIsNullPreview, "CanConvert"), "SQL Server ISNULL replacement should still convert to PostgreSQL.");
        string pgSqlServerIsNullSql = (string)GetProperty(pgSqlServerIsNullPreview, "ConvertedSql");
        AssertContains(pgSqlServerIsNullSql, "COALESCE(display_name, '匿名')", "Converted PostgreSQL SQL should keep two-argument SQL Server ISNULL as COALESCE.");

        object pgFieldPreview = BuildViewSqlPreview(
            "SELECT FIELD(status, 'new', 'active', 'closed') AS status_rank FROM tasks",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgFieldPreview, "CanConvert"), "MySQL FIELD should convert to PostgreSQL.");
        string pgFieldSql = (string)GetProperty(pgFieldPreview, "ConvertedSql");
        AssertContains(pgFieldSql, "CASE status WHEN 'new' THEN 1 WHEN 'active' THEN 2 WHEN 'closed' THEN 3 ELSE 0 END", "Converted PostgreSQL SQL should emulate MySQL FIELD with CASE.");
        AssertNotContains(pgFieldSql, "FIELD(", "Converted PostgreSQL SQL should remove MySQL FIELD.");

        object mssqlFieldPreview = BuildViewSqlPreview(
            "SELECT id FROM tasks ORDER BY FIELD(priority, 'high', 'normal', 'low')",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlFieldPreview, "CanConvert"), "MySQL FIELD in ORDER BY should convert to SQL Server.");
        string mssqlFieldSql = (string)GetProperty(mssqlFieldPreview, "ConvertedSql");
        AssertContains(mssqlFieldSql, "ORDER BY CASE priority WHEN 'high' THEN 1 WHEN 'normal' THEN 2 WHEN 'low' THEN 3 ELSE 0 END", "Converted SQL Server SQL should preserve FIELD ordering semantics with CASE.");

        object pgEltPreview = BuildViewSqlPreview(
            "SELECT ELT(status_rank, 'new', 'active', 'closed') AS status_text FROM tasks",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgEltPreview, "CanConvert"), "MySQL ELT should convert to PostgreSQL.");
        string pgEltSql = (string)GetProperty(pgEltPreview, "ConvertedSql");
        AssertContains(pgEltSql, "CASE status_rank WHEN 1 THEN 'new' WHEN 2 THEN 'active' WHEN 3 THEN 'closed' ELSE NULL END", "Converted PostgreSQL SQL should emulate MySQL ELT with CASE.");
        AssertNotContains(pgEltSql, "ELT(", "Converted PostgreSQL SQL should remove MySQL ELT.");

        object sqliteEltPreview = BuildViewSqlPreview(
            "SELECT ELT(level_no, 'low', 'normal', 'high') AS level_text FROM alerts",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteEltPreview, "CanConvert"), "MySQL ELT should convert to SQLite.");
        string sqliteEltSql = (string)GetProperty(sqliteEltPreview, "ConvertedSql");
        AssertContains(sqliteEltSql, "CASE level_no WHEN 1 THEN 'low' WHEN 2 THEN 'normal' WHEN 3 THEN 'high' ELSE NULL END", "Converted SQLite SQL should emulate MySQL ELT with CASE.");

        object pgFindInSetPreview = BuildViewSqlPreview(
            "SELECT FIND_IN_SET(status, 'new,active,closed') AS status_rank FROM tasks",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgFindInSetPreview, "CanConvert"), "MySQL FIND_IN_SET with literal list should convert to PostgreSQL.");
        string pgFindInSetSql = (string)GetProperty(pgFindInSetPreview, "ConvertedSql");
        AssertContains(pgFindInSetSql, "CASE status WHEN 'new' THEN 1 WHEN 'active' THEN 2 WHEN 'closed' THEN 3 ELSE 0 END", "Converted PostgreSQL SQL should emulate literal FIND_IN_SET with CASE.");
        AssertNotContains(pgFindInSetSql, "FIND_IN_SET", "Converted PostgreSQL SQL should remove literal FIND_IN_SET.");

        object mssqlFindInSetPreview = BuildViewSqlPreview(
            "SELECT id FROM tasks ORDER BY FIND_IN_SET(priority, 'high,normal,low')",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlFindInSetPreview, "CanConvert"), "MySQL FIND_IN_SET in ORDER BY should convert to SQL Server.");
        string mssqlFindInSetSql = (string)GetProperty(mssqlFindInSetPreview, "ConvertedSql");
        AssertContains(mssqlFindInSetSql, "ORDER BY CASE priority WHEN 'high' THEN 1 WHEN 'normal' THEN 2 WHEN 'low' THEN 3 ELSE 0 END", "Converted SQL Server SQL should preserve literal FIND_IN_SET ordering with CASE.");

        object pgStrCmpPreview = BuildViewSqlPreview(
            "SELECT STRCMP(current_code, expected_code) AS compare_result FROM checks",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgStrCmpPreview, "CanConvert"), "MySQL STRCMP should convert to PostgreSQL.");
        string pgStrCmpSql = (string)GetProperty(pgStrCmpPreview, "ConvertedSql");
        AssertContains(pgStrCmpSql, "CASE WHEN current_code = expected_code THEN 0 WHEN current_code < expected_code THEN -1 ELSE 1 END", "Converted PostgreSQL SQL should emulate STRCMP with CASE.");
        AssertNotContains(pgStrCmpSql, "STRCMP", "Converted PostgreSQL SQL should remove MySQL STRCMP.");

        object mssqlStrCmpPreview = BuildViewSqlPreview(
            "SELECT STRCMP(last_name, first_name) AS name_order FROM users",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlStrCmpPreview, "CanConvert"), "MySQL STRCMP should convert to SQL Server.");
        string mssqlStrCmpSql = (string)GetProperty(mssqlStrCmpPreview, "ConvertedSql");
        AssertContains(mssqlStrCmpSql, "CASE WHEN last_name = first_name THEN 0 WHEN last_name < first_name THEN -1 ELSE 1 END", "Converted SQL Server SQL should emulate STRCMP with CASE.");

        object pgChoosePreview = BuildViewSqlPreview(
            "SELECT CHOOSE(status_rank, 'new', 'active', 'closed') AS status_text FROM tasks",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgChoosePreview, "CanConvert"), "SQL Server CHOOSE should convert to PostgreSQL.");
        string pgChooseSql = (string)GetProperty(pgChoosePreview, "ConvertedSql");
        AssertContains(pgChooseSql, "CASE status_rank WHEN 1 THEN 'new' WHEN 2 THEN 'active' WHEN 3 THEN 'closed' ELSE NULL END", "Converted PostgreSQL SQL should emulate SQL Server CHOOSE with CASE.");
        AssertNotContains(pgChooseSql, "CHOOSE", "Converted PostgreSQL SQL should remove SQL Server CHOOSE.");

        object sqliteChoosePreview = BuildViewSqlPreview(
            "SELECT CHOOSE(level_no, 'low', 'normal', 'high') AS level_text FROM alerts",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteChoosePreview, "CanConvert"), "SQL Server CHOOSE should convert to SQLite.");
        string sqliteChooseSql = (string)GetProperty(sqliteChoosePreview, "ConvertedSql");
        AssertContains(sqliteChooseSql, "CASE level_no WHEN 1 THEN 'low' WHEN 2 THEN 'normal' WHEN 3 THEN 'high' ELSE NULL END", "Converted SQLite SQL should emulate SQL Server CHOOSE with CASE.");

        object mysqlStuffPreview = BuildViewSqlPreview(
            "SELECT STUFF(code, 2, 3, '***') AS masked_code FROM items",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlStuffPreview, "CanConvert"), "SQL Server STUFF should convert to MySQL.");
        string mysqlStuffSql = (string)GetProperty(mysqlStuffPreview, "ConvertedSql");
        AssertContains(mysqlStuffSql, "CONCAT(SUBSTRING(code, 1, 2 - 1), '***', SUBSTRING(code, 2 + 3))", "Converted MySQL SQL should emulate STUFF with substring concatenation.");
        AssertNotContains(mysqlStuffSql, "STUFF", "Converted MySQL SQL should remove STUFF.");

        object sqliteStuffPreview = BuildViewSqlPreview(
            "SELECT STUFF(code, 2, 3, '***') AS masked_code FROM items",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqliteStuffPreview, "CanConvert"), "SQL Server STUFF should convert to SQLite.");
        string sqliteStuffSql = (string)GetProperty(sqliteStuffPreview, "ConvertedSql");
        AssertContains(sqliteStuffSql, "SUBSTR(code, 1, 2 - 1) || '***' || SUBSTR(code, 2 + 3)", "Converted SQLite SQL should emulate STUFF with SUBSTR concatenation.");
        AssertNotContains(sqliteStuffSql, "STUFF", "Converted SQLite SQL should remove STUFF.");

        object pgNestedStuffLiteralPreview = BuildViewSqlPreview(
            "SELECT STUFF(REPLACE(code, '-', ''), 2, 3, '***') AS masked_code, 'STUFF(REPLACE(code, ''-'', ''''), 2, 3, ''***'')' AS literal_note FROM items",
            "mssql",
            "postgresql");
        Assert((bool)GetProperty(pgNestedStuffLiteralPreview, "CanConvert"), "Nested SQL Server STUFF should convert while preserving literals.");
        string pgNestedStuffLiteralSql = (string)GetProperty(pgNestedStuffLiteralPreview, "ConvertedSql");
        AssertContains(pgNestedStuffLiteralSql, "CONCAT(SUBSTRING(REPLACE(code, '-', '') FROM 1 FOR 2 - 1), '***', SUBSTRING(REPLACE(code, '-', '') FROM 2 + 3)) AS masked_code", "Converted PostgreSQL SQL should convert nested STUFF expression.");
        AssertContains(pgNestedStuffLiteralSql, "'STUFF(REPLACE(code, ''-'', ''''), 2, 3, ''***'')' AS literal_note", "Converted PostgreSQL SQL should preserve STUFF text inside string literals.");

        object pgSubstringIndexPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING_INDEX(host_name, '.', 1) AS root_host FROM servers",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgSubstringIndexPreview, "CanConvert"), "MySQL SUBSTRING_INDEX should convert to PostgreSQL.");
        string pgSubstringIndexSql = (string)GetProperty(pgSubstringIndexPreview, "ConvertedSql");
        AssertContains(pgSubstringIndexSql, "CASE WHEN POSITION('.' IN host_name) = 0 THEN host_name ELSE SUBSTRING(host_name FROM 1 FOR POSITION('.' IN host_name) - 1) END", "Converted PostgreSQL SQL should emulate SUBSTRING_INDEX count 1.");
        AssertNotContains(pgSubstringIndexSql, "SUBSTRING_INDEX", "Converted PostgreSQL SQL should remove SUBSTRING_INDEX.");

        object pgNestedSubstringIndexLiteralPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING_INDEX(REPLACE(host_name, 'www.', ''), '.', 1) AS root_host, 'SUBSTRING_INDEX(REPLACE(host_name, ''www.'', ''''), ''.'', 1)' AS literal_note FROM servers",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgNestedSubstringIndexLiteralPreview, "CanConvert"), "Nested MySQL SUBSTRING_INDEX should convert while preserving literals.");
        string pgNestedSubstringIndexLiteralSql = (string)GetProperty(pgNestedSubstringIndexLiteralPreview, "ConvertedSql");
        AssertContains(pgNestedSubstringIndexLiteralSql, "CASE WHEN POSITION('.' IN REPLACE(host_name, 'www.', '')) = 0 THEN REPLACE(host_name, 'www.', '') ELSE SUBSTRING(REPLACE(host_name, 'www.', '') FROM 1 FOR POSITION('.' IN REPLACE(host_name, 'www.', '')) - 1) END AS root_host", "Converted PostgreSQL SQL should convert nested SUBSTRING_INDEX expression.");
        AssertContains(pgNestedSubstringIndexLiteralSql, "'SUBSTRING_INDEX(REPLACE(host_name, ''www.'', ''''), ''.'', 1)' AS literal_note", "Converted PostgreSQL SQL should preserve SUBSTRING_INDEX text inside string literals.");

        object mssqlSubstringIndexPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING_INDEX(file_name, '/', 1) AS top_folder FROM files",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlSubstringIndexPreview, "CanConvert"), "MySQL SUBSTRING_INDEX should convert to SQL Server.");
        string mssqlSubstringIndexSql = (string)GetProperty(mssqlSubstringIndexPreview, "ConvertedSql");
        AssertContains(mssqlSubstringIndexSql, "CASE WHEN CHARINDEX('/', file_name) = 0 THEN file_name ELSE LEFT(file_name, CHARINDEX('/', file_name) - 1) END", "Converted SQL Server SQL should emulate SUBSTRING_INDEX count 1.");

        object sqliteSubstringIndexPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING_INDEX(tag_path, '/', 1) AS root_tag FROM tags",
            "mysql",
            "sqlite");
        Assert((bool)GetProperty(sqliteSubstringIndexPreview, "CanConvert"), "MySQL SUBSTRING_INDEX should convert to SQLite.");
        string sqliteSubstringIndexSql = (string)GetProperty(sqliteSubstringIndexPreview, "ConvertedSql");
        AssertContains(sqliteSubstringIndexSql, "CASE WHEN INSTR(tag_path, '/') = 0 THEN tag_path ELSE SUBSTR(tag_path, 1, INSTR(tag_path, '/') - 1) END", "Converted SQLite SQL should emulate SUBSTRING_INDEX count 1.");

        object mssqlSubstringIndexLastPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING_INDEX(file_path, '/', -1) AS file_name FROM files",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlSubstringIndexLastPreview, "CanConvert"), "MySQL SUBSTRING_INDEX count -1 should convert to SQL Server.");
        string mssqlSubstringIndexLastSql = (string)GetProperty(mssqlSubstringIndexLastPreview, "ConvertedSql");
        AssertContains(mssqlSubstringIndexLastSql, "CASE WHEN CHARINDEX('/', file_path) = 0 THEN file_path ELSE RIGHT(file_path, CHARINDEX(REVERSE('/'), REVERSE(file_path)) - 1) END", "Converted SQL Server SQL should emulate SUBSTRING_INDEX count -1.");
        AssertNotContains(mssqlSubstringIndexLastSql, "SUBSTRING_INDEX", "Converted SQL Server SQL should remove SUBSTRING_INDEX count -1.");

        object pgSubstringIndexLastPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING_INDEX(host_name, '.', -1) AS tld FROM servers",
            "mysql",
            "postgresql");
        Assert((bool)GetProperty(pgSubstringIndexLastPreview, "CanConvert"), "MySQL SUBSTRING_INDEX count -1 should convert to PostgreSQL.");
        string pgSubstringIndexLastSql = (string)GetProperty(pgSubstringIndexLastPreview, "ConvertedSql");
        AssertContains(pgSubstringIndexLastSql, "CASE WHEN POSITION('.' IN host_name) = 0 THEN host_name ELSE RIGHT(host_name, POSITION(reverse('.') IN reverse(host_name)) - 1) END", "Converted PostgreSQL SQL should emulate SUBSTRING_INDEX count -1.");

        object oracleSubstringIndexLastPreview = BuildViewSqlPreview(
            "SELECT SUBSTRING_INDEX(object_name, '.', -1) AS short_name FROM objects",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(oracleSubstringIndexLastPreview, "CanConvert"), "MySQL SUBSTRING_INDEX count -1 should convert to Oracle.");
        string oracleSubstringIndexLastSql = (string)GetProperty(oracleSubstringIndexLastPreview, "ConvertedSql");
        AssertContains(oracleSubstringIndexLastSql, "CASE WHEN INSTR(object_name, '.') = 0 THEN object_name ELSE SUBSTR(object_name, INSTR(object_name, '.', -1) + LENGTH('.')) END", "Converted Oracle SQL should emulate SUBSTRING_INDEX count -1.");

        object mssqlPositionPreview = BuildViewSqlPreview(
            "SELECT LOCATE('@', email) AS at_pos FROM users",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlPositionPreview, "CanConvert"), "MySQL LOCATE should convert to SQL Server.");
        string mssqlPositionSql = (string)GetProperty(mssqlPositionPreview, "ConvertedSql");
        AssertContains(mssqlPositionSql, "CHARINDEX('@', email)", "Converted SQL Server SQL should use CHARINDEX.");

        object pgPositionPreview = BuildViewSqlPreview(
            "SELECT INSTR(email, '@') AS at_pos FROM users",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgPositionPreview, "CanConvert"), "Oracle INSTR should convert to PostgreSQL.");
        string pgPositionSql = (string)GetProperty(pgPositionPreview, "ConvertedSql");
        AssertContains(pgPositionSql, "POSITION('@' IN email)", "Converted PostgreSQL SQL should use POSITION.");

        object sqlitePositionPreview = BuildViewSqlPreview(
            "SELECT CHARINDEX('@', email) AS at_pos FROM users",
            "mssql",
            "sqlite");
        Assert((bool)GetProperty(sqlitePositionPreview, "CanConvert"), "SQL Server CHARINDEX should convert to SQLite.");
        string sqlitePositionSql = (string)GetProperty(sqlitePositionPreview, "ConvertedSql");
        AssertContains(sqlitePositionSql, "INSTR(email, '@')", "Converted SQLite SQL should use INSTR.");

        object mysqlPositionStartPreview = BuildViewSqlPreview(
            "SELECT CHARINDEX('@', email, 3) AS at_pos FROM users",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlPositionStartPreview, "CanConvert"), "SQL Server CHARINDEX with start should convert to MySQL.");
        string mysqlPositionStartSql = (string)GetProperty(mysqlPositionStartPreview, "ConvertedSql");
        AssertContains(mysqlPositionStartSql, "LOCATE('@', email, 3)", "Converted MySQL SQL should preserve CHARINDEX start position.");

        object sqlitePositionStartPreview = BuildViewSqlPreview(
            "SELECT INSTR(email, '@', 3) AS at_pos FROM users",
            "oracle",
            "sqlite");
        Assert((bool)GetProperty(sqlitePositionStartPreview, "CanConvert"), "Oracle INSTR with start should convert to SQLite.");
        string sqlitePositionStartSql = (string)GetProperty(sqlitePositionStartPreview, "ConvertedSql");
        AssertContains(sqlitePositionStartSql, "CASE WHEN INSTR(SUBSTR(email, 3), '@') = 0 THEN 0 ELSE INSTR(SUBSTR(email, 3), '@') + 3 - 1 END", "Converted SQLite SQL should preserve INSTR start position.");

        object mysqlIlikePreview = BuildViewSqlPreview(
            "SELECT id FROM users WHERE email ILIKE '%@example.com'",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlIlikePreview, "CanConvert"), "PostgreSQL ILIKE should convert to MySQL.");
        string mysqlIlikeSql = (string)GetProperty(mysqlIlikePreview, "ConvertedSql");
        AssertContains(mysqlIlikeSql, "LOWER(email) LIKE LOWER('%@example.com')", "Converted MySQL SQL should use case-insensitive LIKE fallback.");
        AssertNotContains(mysqlIlikeSql, "ILIKE", "Converted MySQL SQL should remove ILIKE.");

        object mssqlIlikePreview = BuildViewSqlPreview(
            "SELECT id FROM users WHERE name ILIKE 'O''Reilly%'",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(mssqlIlikePreview, "CanConvert"), "PostgreSQL ILIKE should convert to SQL Server.");
        string mssqlIlikeSql = (string)GetProperty(mssqlIlikePreview, "ConvertedSql");
        AssertContains(mssqlIlikeSql, "LOWER(name) LIKE LOWER('O''Reilly%')", "Converted SQL Server SQL should preserve escaped quote patterns.");

        object pgRegexpLikePreview = BuildViewSqlPreview(
            "SELECT id FROM users WHERE REGEXP_LIKE(email, '^[^@]+@example\\.com$')",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgRegexpLikePreview, "CanConvert"), "Oracle REGEXP_LIKE should convert to PostgreSQL.");
        string pgRegexpLikeSql = (string)GetProperty(pgRegexpLikePreview, "ConvertedSql");
        AssertContains(pgRegexpLikeSql, "email ~ '^[^@]+@example\\.com$'", "Converted PostgreSQL SQL should use regex match operator.");

        object mysqlPgRegexPreview = BuildViewSqlPreview(
            "SELECT id FROM users WHERE email ~ '^[^@]+@example\\.com$'",
            "postgresql",
            "mysql");
        Assert((bool)GetProperty(mysqlPgRegexPreview, "CanConvert"), "PostgreSQL regex operator should convert to MySQL.");
        string mysqlPgRegexSql = (string)GetProperty(mysqlPgRegexPreview, "ConvertedSql");
        AssertContains(mysqlPgRegexSql, "REGEXP_LIKE(email, '^[^@]+@example\\.com$')", "Converted MySQL SQL should use REGEXP_LIKE.");

        object mssqlRegexpLikePreview = BuildViewSqlPreview(
            "SELECT id FROM users WHERE REGEXP_LIKE(email, '^[^@]+@example\\.com$')",
            "oracle",
            "mssql");
        Assert(!(bool)GetProperty(mssqlRegexpLikePreview, "CanConvert"), "REGEXP_LIKE should be rejected for SQL Server.");
        string regexpReason = (string)GetProperty(mssqlRegexpLikePreview, "Reason");
        AssertContains(regexpReason, "正規表示式", "Regex rejection should explain unsupported regex conversion.");

        Localization.SetLanguage(Localization.TraditionalChinese, false);
        object cteWindowPreview = BuildViewSqlPreview(
            "WITH ranked AS (SELECT id, ROW_NUMBER() OVER (PARTITION BY group_id ORDER BY created_at DESC) AS rn FROM items) SELECT id FROM ranked WHERE rn = 1",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(cteWindowPreview, "CanConvert"), "Portable CTE/window SQL should be preserved for SQL Server.");
        string cteWindowSql = (string)GetProperty(cteWindowPreview, "ConvertedSql");
        AssertContains(cteWindowSql, "WITH ranked AS", "Converted SQL should preserve CTE.");
        AssertContains(cteWindowSql, "ROW_NUMBER() OVER", "Converted SQL should preserve window function.");

        object recursiveCtePreview = BuildViewSqlPreview(
            "WITH RECURSIVE tree AS (SELECT id, parent_id FROM nodes WHERE parent_id IS NULL UNION ALL SELECT n.id, n.parent_id FROM nodes n JOIN tree t ON n.parent_id = t.id) SELECT id FROM tree",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(recursiveCtePreview, "CanConvert"), "Recursive CTE keyword should convert for SQL Server.");
        string recursiveCteSql = (string)GetProperty(recursiveCtePreview, "ConvertedSql");
        AssertContains(recursiveCteSql, "WITH tree AS", "Converted SQL Server SQL should remove unsupported RECURSIVE keyword.");
        AssertNotContains(recursiveCteSql, "WITH RECURSIVE", "Converted SQL Server SQL should not keep WITH RECURSIVE.");

        object unsupportedPreview = BuildViewSqlPreview(
            "CREATE VIEW v AS SELECT id FROM employee START WITH manager_id IS NULL CONNECT BY PRIOR id = manager_id",
            "oracle",
            "mysql");
        Assert(!(bool)GetProperty(unsupportedPreview, "CanConvert"), "Oracle hierarchical query should be rejected instead of converted silently.");
        string reason = (string)GetProperty(unsupportedPreview, "Reason");
        AssertContains(reason, "Oracle 階層查詢", "Unsupported conversion should return a localized Traditional Chinese reason.");

        Localization.SetLanguage(Localization.English, false);
        try
        {
            object parseFailedPreview = BuildViewSqlPreview(
                "CREATE VIEW v AS BROKEN SQL",
                "mysql",
                "postgresql");
            Assert(!(bool)GetProperty(parseFailedPreview, "CanConvert"), "Malformed View SQL should not convert.");
            AssertContains((string)GetProperty(parseFailedPreview, "Reason"), "Cannot parse SELECT SQL", "Parse failure reason should localize to English.");

            object unsafeOffsetPreview = BuildViewSqlPreview(
                "SELECT id, name FROM users LIMIT 10 OFFSET 5",
                "mysql",
                "mssql");
            Assert(!(bool)GetProperty(unsafeOffsetPreview, "CanConvert"), "Unsafe SQL Server offset conversion should still be rejected.");
            AssertContains((string)GetProperty(unsafeOffsetPreview, "Reason"), "requires a stable ORDER BY", "LIMIT OFFSET reason should localize to English.");

            object complexRownumPreview = BuildViewSqlPreview(
                "SELECT id FROM users WHERE (ROWNUM <= 10 OR status = 'active')",
                "oracle",
                "mysql");
            Assert(!(bool)GetProperty(complexRownumPreview, "CanConvert"), "Complex ROWNUM predicates should still be rejected.");
            AssertContains((string)GetProperty(complexRownumPreview, "Reason"), "too complex", "ROWNUM reason should localize to English.");

            object topPreview = BuildViewSqlPreview(
                "SELECT id FROM (SELECT TOP 5 id FROM users) t",
                "mssql",
                "mysql");
            Assert(!(bool)GetProperty(topPreview, "CanConvert"), "Unrewritten TOP syntax should still be rejected.");
            AssertContains((string)GetProperty(topPreview, "Reason"), "TOP syntax is not portable", "TOP reason should localize to English.");

            object oracleUnsupportedPreview = BuildViewSqlPreview(
                "CREATE VIEW v AS SELECT id FROM employee START WITH manager_id IS NULL CONNECT BY PRIOR id = manager_id",
                "oracle",
                "mysql");
            Assert(!(bool)GetProperty(oracleUnsupportedPreview, "CanConvert"), "Oracle hierarchical query should still be rejected in English.");
            AssertContains((string)GetProperty(oracleUnsupportedPreview, "Reason"), "Oracle hierarchical queries", "Oracle hierarchy reason should localize to English.");

            object mysqlSpecificPreview = BuildViewSqlPreview(
                "SELECT @counter := @counter + 1 AS seq_no, id FROM users",
                "mysql",
                "postgresql");
            Assert(!(bool)GetProperty(mysqlSpecificPreview, "CanConvert"), "MySQL-specific View syntax should still be rejected.");
            AssertContains((string)GetProperty(mysqlSpecificPreview, "Reason"), "MySQL-specific View syntax", "MySQL-specific reason should localize to English.");

            object englishRegexpLikePreview = BuildViewSqlPreview(
                "SELECT id FROM users WHERE REGEXP_LIKE(email, '^[^@]+@example\\.com$')",
                "oracle",
                "mssql");
            Assert(!(bool)GetProperty(englishRegexpLikePreview, "CanConvert"), "REGEXP_LIKE should still be rejected for SQL Server in English.");
            AssertContains((string)GetProperty(englishRegexpLikePreview, "Reason"), "regular expression", "Regex reason should localize to English.");

            object jsonTableUnsupportedPreview = BuildViewSqlPreview(
                "SELECT jt.seq_no FROM orders o CROSS JOIN JSON_TABLE(o.payload, '$.items[*]' COLUMNS (seq_no FOR ORDINALITY)) jt",
                "mysql",
                "mssql");
            Assert(!(bool)GetProperty(jsonTableUnsupportedPreview, "CanConvert"), "Unsupported JSON_TABLE shape should still be rejected.");
            AssertContains((string)GetProperty(jsonTableUnsupportedPreview, "Reason"), "JSON_TABLE syntax cannot be safely converted", "JSON_TABLE reason should localize to English.");
        }
        finally
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
        }
    }

    private static object BuildViewSqlPreview(string sql, string sourceProvider, string targetProvider)
    {
        Type converter = typeof(DatabaseCopyService).Assembly.GetType("mySQLPunk.lib.ViewSqlDialectConverter", true);
        MethodInfo method = converter.GetMethod("BuildPreview", BindingFlags.Public | BindingFlags.Static);
        return method.Invoke(null, new object[] { sql, sourceProvider, targetProvider });
    }

    private static object GetProperty(object target, string name)
    {
        return target.GetType().GetProperty(name).GetValue(target, null);
    }

    private static void TestSqliteSpecialObjectSqlBuilder()
    {
        string ftsSql = SqliteSpecialObjectSqlBuilder.BuildFtsVirtualTable(
            "doc_search",
            SqliteSpecialObjectSqlBuilder.SplitCommaSeparatedNames("title, body"),
            "unicode61",
            "documents");
        AssertContains(ftsSql, "CREATE VIRTUAL TABLE \"doc_search\" USING fts5", "FTS SQL should create an fts5 virtual table.");
        AssertContains(ftsSql, "\"title\"", "FTS SQL should quote title column.");
        AssertContains(ftsSql, "tokenize = 'unicode61'", "FTS SQL should include tokenizer.");
        AssertContains(ftsSql, "content = 'documents'", "FTS SQL should include content table.");

        string rtreeSql = SqliteSpecialObjectSqlBuilder.BuildRTreeVirtualTable(
            "idx_boxes",
            "id",
            SqliteSpecialObjectSqlBuilder.SplitCommaSeparatedNames("minX, maxX, minY, maxY"));
        AssertContains(rtreeSql, "CREATE VIRTUAL TABLE \"idx_boxes\" USING rtree", "RTree SQL should create an rtree virtual table.");
        AssertContains(rtreeSql, "\"minX\"", "RTree SQL should quote dimension columns.");

        string spatialSql = SqliteSpecialObjectSqlBuilder.BuildSpatiaLiteSpatialIndex("places", "geom");
        AssertEquals("SELECT CreateSpatialIndex('places', 'geom');", spatialSql, "SpatiaLite spatial index SQL should call CreateSpatialIndex.");

        string reviewLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            try
            {
                SqliteSpecialObjectSqlBuilder.BuildRTreeVirtualTable("bad", "id", SqliteSpecialObjectSqlBuilder.SplitCommaSeparatedNames("minX, maxX, minY"));
                Assert(false, "RTree builder should reject incomplete min/max dimension pairs.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "RTree 維度欄位必須包含 min/max 成對欄位", "RTree dimension validation should localize Traditional Chinese messages.");
            }

            try
            {
                SqliteSpecialObjectSqlBuilder.BuildFtsVirtualTable("doc_search", new string[0], "unicode61", "");
                Assert(false, "FTS builder should require at least one column.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "請填寫 columns", "SQLite special object column validation should localize Traditional Chinese messages.");
            }

            Localization.SetLanguage(Localization.English, false);
            try
            {
                SqliteSpecialObjectSqlBuilder.BuildSpatiaLiteSpatialIndex("", "geom");
                Assert(false, "SpatiaLite spatial index builder should require a table name.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "tableName is required", "SQLite special object name validation should localize English messages.");
            }
        }
        finally
        {
            Localization.SetLanguage(reviewLanguage, false);
        }
    }

    private static void TestSqliteSpecialObjectWizardExecutionFallback()
    {
        MethodInfo reasonMethod = typeof(SqliteSpecialObjectWizardForm).GetMethod("BuildExecutionFailureReason", BindingFlags.NonPublic | BindingFlags.Static);
        Assert(reasonMethod != null, "SQLite special object wizard should expose a testable execution fallback helper.");
        MethodInfo exceptionReasonMethod = typeof(SqliteSpecialObjectWizardForm).GetMethod("BuildExceptionFailureReason", BindingFlags.NonPublic | BindingFlags.Static);
        Assert(exceptionReasonMethod != null, "SQLite special object wizard should expose a testable exception fallback helper.");

        string reviewLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            Dictionary<string, string> failedWithoutReason = new Dictionary<string, string>
            {
                { "status", "error" }
            };
            AssertContains((string)reasonMethod.Invoke(null, new object[] { failedWithoutReason }), "SQL 執行失敗", "SQLite wizard execution fallback should localize Traditional Chinese messages.");

            Dictionary<string, string> failedWithReason = new Dictionary<string, string>
            {
                { "status", "error" },
                { "reason", "virtual table already exists" }
            };
            AssertContains((string)reasonMethod.Invoke(null, new object[] { failedWithReason }), "virtual table already exists", "SQLite wizard execution fallback should preserve provider reasons.");
            AssertEquals("未知錯誤", (string)exceptionReasonMethod.Invoke(null, new object[] { new Exception("") }), "SQLite wizard blank exceptions should localize Traditional Chinese unknown errors.");

            Localization.SetLanguage(Localization.English, false);
            AssertContains((string)reasonMethod.Invoke(null, new object[] { failedWithoutReason }), "SQL execution failed", "SQLite wizard execution fallback should localize English messages.");
            AssertEquals("Unknown error", (string)exceptionReasonMethod.Invoke(null, new object[] { new Exception("   ") }), "SQLite wizard blank exceptions should localize English unknown errors.");
            AssertEquals("parser failed", (string)exceptionReasonMethod.Invoke(null, new object[] { new InvalidOperationException(" parser failed ") }), "SQLite wizard exception fallback should preserve explicit exception messages.");
        }
        finally
        {
            Localization.SetLanguage(reviewLanguage, false);
        }
    }

    private static void TestTableDesignerDdlBuilder()
    {
        string mysqlSql = BuildCreateTableSql(new my_mysql(), "codex_smoke_mysql");
        AssertContains(mysqlSql, "CREATE TABLE", "MySQL create table SQL should be generated.");
        AssertContains(mysqlSql, "codex_smoke_mysql", "MySQL create table SQL should include table name.");
        AssertContains(mysqlSql, "PRIMARY KEY", "MySQL create table SQL should include primary key.");

        string sqliteSql = BuildCreateTableSql(new my_sqlite(), "codex_smoke_sqlite");
        AssertContains(sqliteSql, "CREATE TABLE", "SQLite create table SQL should be generated.");
        AssertContains(sqliteSql, "codex_smoke_sqlite", "SQLite create table SQL should include table name.");
        AssertContains(sqliteSql, "__mysqlpunk_column_comments", "SQLite create table SQL should include sidecar comments when comments exist.");

        DataTable advancedIndexes = CreateDesignerIndexesTable();
        AddDesignerIndex(advancedIndexes, "idx_docs_body_ft", "body, title", "FULLTEXT", "GIN", "");
        AddDesignerIndex(advancedIndexes, "idx_docs_geom", "geom", "SPATIAL", "GIST", "");

        string postgresqlIndexSql = BuildGenericCreateIndexSql(
            CreateProvider<my_postgresql>(),
            "main",
            "public.docs",
            advancedIndexes);
        AssertContains(postgresqlIndexSql, "USING GIN", "PostgreSQL FULLTEXT index should use GIN.");
        AssertContains(postgresqlIndexSql, "to_tsvector('simple'", "PostgreSQL FULLTEXT index should build a tsvector expression.");
        AssertContains(postgresqlIndexSql, "USING GIST", "PostgreSQL SPATIAL index should use GIST.");

        string sqlServerIndexSql = BuildGenericCreateIndexSql(
            CreateProvider<my_mssql>(),
            "main",
            "dbo.docs",
            advancedIndexes);
        AssertContains(sqlServerIndexSql, "CREATE FULLTEXT INDEX ON [dbo].[docs]", "SQL Server FULLTEXT index should create a full-text index statement.");
        AssertContains(sqlServerIndexSql, "[body] LANGUAGE 0x0", "SQL Server FULLTEXT index should include language-neutral columns.");
        AssertContains(sqlServerIndexSql, "CREATE SPATIAL INDEX [idx_docs_geom]", "SQL Server SPATIAL index should create a spatial index.");

        string oracleIndexSql = BuildGenericCreateIndexSql(
            CreateProvider<my_oracle>(),
            "MAIN",
            "DOCS",
            advancedIndexes);
        AssertContains(oracleIndexSql, "INDEXTYPE IS CTXSYS.CONTEXT", "Oracle FULLTEXT index should use CTXSYS.CONTEXT.");
        AssertContains(oracleIndexSql, "INDEXTYPE IS MDSYS.SPATIAL_INDEX", "Oracle SPATIAL index should use MDSYS.SPATIAL_INDEX.");

        string oldLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            MethodInfo saveErrorMethod = typeof(TableDesignerForm).GetMethod("FormatDesignerSaveError", BindingFlags.Static | BindingFlags.NonPublic);
            string saveErrorMessage = (string)saveErrorMethod.Invoke(null, new object[] { "mysql", "main", "codex_smoke_mysql", new Dictionary<string, string>() });
            AssertContains(saveErrorMessage, "儲存失敗：未知錯誤", "Table designer save failure should localize missing reasons in Traditional Chinese.");
            string indexLoadFailedMessage = TableDesignerForm.BuildIndexMetadataLoadFailedMessage(new InvalidOperationException("metadata timeout"));
            AssertContains(indexLoadFailedMessage, "metadata timeout", "Index metadata load failure should include the provider error.");
            AssertContains(indexLoadFailedMessage, "索引頁", "Index metadata load failure should explain that the index page remains usable.");
            string unknownIndexLoadFailedMessage = TableDesignerForm.BuildIndexMetadataLoadFailedMessage(null);
            AssertContains(unknownIndexLoadFailedMessage, "未知錯誤", "Index metadata fallback should localize missing errors in Traditional Chinese.");
            string columnLoadFailedMessage = TableDesignerForm.BuildColumnMetadataLoadFailedMessage(new InvalidOperationException("columns timeout"));
            AssertContains(columnLoadFailedMessage, "無法載入欄位資訊", "Column metadata load failure should localize Traditional Chinese messages.");
            AssertContains(columnLoadFailedMessage, "columns timeout", "Column metadata load failure should include the provider error.");

            Localization.SetLanguage(Localization.English, false);
            string englishSaveErrorMessage = (string)saveErrorMethod.Invoke(null, new object[] { "mysql", "main", "codex_smoke_mysql", new Dictionary<string, string>() });
            AssertContains(englishSaveErrorMessage, "Save failed: Unknown error", "Table designer save failure should localize missing reasons in English.");
            string englishUnknownIndexLoadFailedMessage = TableDesignerForm.BuildIndexMetadataLoadFailedMessage(null);
            AssertContains(englishUnknownIndexLoadFailedMessage, "Unknown error", "Index metadata fallback should localize missing errors in English.");
            string englishColumnLoadFailedMessage = TableDesignerForm.BuildColumnMetadataLoadFailedMessage(new InvalidOperationException("columns timeout"));
            AssertContains(englishColumnLoadFailedMessage, "Cannot load column metadata", "Column metadata load failure should localize English messages.");
            AssertContains(englishColumnLoadFailedMessage, "columns timeout", "English column metadata load failure should include the provider error.");
        }
        finally
        {
            Localization.SetLanguage(oldLanguage, false);
        }
    }

    private static string BuildCreateTableSql(IDatabase db, string tableName)
    {
        using (TableDesignerForm form = new TableDesignerForm(db, "main", null))
        {
            SetTextBoxField(form, "txtTableName", tableName);

            MethodInfo createTableMethod = typeof(TableDesignerForm).GetMethod("CreateColumnsDisplayTable", BindingFlags.Instance | BindingFlags.NonPublic);
            DataTable columns = (DataTable)createTableMethod.Invoke(form, null);

            DataRow row = columns.NewRow();
            row["Name"] = "Id";
            row["Type"] = "int";
            row["NotNull"] = true;
            row["PK"] = true;
            row["Comment"] = "識別碼";
            columns.Rows.Add(row);

            MethodInfo buildMethod = typeof(TableDesignerForm).GetMethod("BuildCreateTableSql", BindingFlags.Instance | BindingFlags.NonPublic);
            return (string)buildMethod.Invoke(form, new object[] { columns });
        }
    }

    private static void TestTableDesignerAlterProviderMatrix()
    {
        string mysqlSql = BuildExistingAlterSql(
            new my_mysql(),
            "main",
            "demo_table",
            CreateOriginalColumnsForAlter(includeRemovedColumn: true),
            CreateChangedColumnsForAlter(includeRemovedColumn: false),
            "BuildMySqlAlterTableSql");
        AssertContains(mysqlSql, "ALTER TABLE `main`.`demo_table`", "MySQL ALTER should target the existing table.");
        AssertContains(mysqlSql, "DROP COLUMN `removed_col`", "MySQL ALTER should drop removed columns.");
        AssertContains(mysqlSql, "CHANGE COLUMN `legacy_name` `display_name`", "MySQL ALTER should rename changed columns.");
        AssertContains(mysqlSql, "ADD COLUMN `created_at`", "MySQL ALTER should add new columns.");

        string postgresqlSql = BuildExistingAlterSql(
            CreateProvider<my_postgresql>(),
            "main",
            "public.demo_table",
            CreateOriginalColumnsForAlter(includeRemovedColumn: false),
            CreateChangedColumnsForAlter(includeRemovedColumn: false),
            "BuildGenericAlterTableSql");
        AssertContains(postgresqlSql, "RENAME COLUMN \"legacy_name\" TO \"display_name\"", "PostgreSQL ALTER should rename columns.");
        AssertContains(postgresqlSql, "ALTER COLUMN \"display_name\" TYPE VARCHAR(120)", "PostgreSQL ALTER should change column type.");
        AssertContains(postgresqlSql, "ALTER COLUMN \"display_name\" SET NOT NULL", "PostgreSQL ALTER should change nullability.");
        AssertContains(postgresqlSql, "ALTER COLUMN \"display_name\" SET DEFAULT", "PostgreSQL ALTER should change defaults.");
        AssertContains(postgresqlSql, "COMMENT ON COLUMN \"public\".\"demo_table\".\"display_name\"", "PostgreSQL ALTER should update comments.");

        string sqlServerSql = BuildExistingAlterSql(
            CreateProvider<my_mssql>(),
            "main",
            "dbo.demo_table",
            CreateOriginalColumnsForAlter(includeRemovedColumn: false),
            CreateChangedColumnsForAlter(includeRemovedColumn: false),
            "BuildGenericAlterTableSql");
        AssertContains(sqlServerSql, "sys.sp_rename", "SQL Server ALTER should rename columns.");
        AssertContains(sqlServerSql, "ALTER COLUMN [display_name] NVARCHAR(120) NOT NULL", "SQL Server ALTER should change type and nullability.");
        AssertContains(sqlServerSql, "sys.default_constraints", "SQL Server ALTER should drop existing default constraints safely.");
        AssertContains(sqlServerSql, "ADD CONSTRAINT [DF_demo_table_display_name]", "SQL Server ALTER should add named default constraints.");
        AssertContains(sqlServerSql, "sp_addextendedproperty", "SQL Server ALTER should update column comments.");

        string oracleSql = BuildExistingAlterSql(
            CreateProvider<my_oracle>(),
            "MAIN",
            "DEMO_TABLE",
            CreateOriginalColumnsForAlter(includeRemovedColumn: false),
            CreateChangedColumnsForAlter(includeRemovedColumn: false),
            "BuildGenericAlterTableSql");
        AssertContains(oracleSql, "RENAME COLUMN \"legacy_name\" TO \"display_name\"", "Oracle ALTER should rename columns.");
        AssertContains(oracleSql, "MODIFY (\"display_name\" VARCHAR2(120)", "Oracle ALTER should modify column definitions.");
        AssertContains(oracleSql, "COMMENT ON COLUMN \"MAIN\".\"DEMO_TABLE\".\"display_name\"", "Oracle ALTER should update comments.");
        string oraclePreview = BuildOraclePreviewNotice(oracleSql, "MAIN", "DEMO_TABLE");
        AssertContains(oraclePreview, "執行前逐步確認清單", "Oracle preview should include an action checklist.");
        AssertContains(oraclePreview, "欄位重新命名", "Oracle preview checklist should include rename column checks.");
        AssertContains(oraclePreview, "欄位型別", "Oracle preview checklist should include modify column checks.");
        AssertContains(oraclePreview, "欄位註解內容", "Oracle preview checklist should include comment checks.");
        AssertContains(oraclePreview, "權限診斷 SQL", "Oracle preview should include privilege diagnostic guidance.");
        AssertContains(oraclePreview, "FROM all_tab_privs", "Oracle preview should include object privilege diagnostic query.");
        AssertContains(oraclePreview, "FROM session_privs", "Oracle preview should include system privilege diagnostic query.");
        AssertContains(oraclePreview, "UPPER(owner) = UPPER('MAIN')", "Oracle privilege diagnostic should target the selected schema.");
        string oraclePrivilegeSummary = BuildOraclePrivilegeDiagnosticSummary(
            BuildOraclePrivilegeTable("ALTER"),
            BuildOraclePrivilegeTable("CREATE ANY INDEX"),
            oracleSql);
        AssertContains(oraclePrivilegeSummary, "物件直接授權：ALTER", "Oracle privilege parser should summarize direct object grants.");
        AssertContains(oraclePrivilegeSummary, "Session 系統權限：CREATE ANY INDEX", "Oracle privilege parser should summarize session privileges.");
        AssertContains(oraclePrivilegeSummary, "可能缺少：COMMENT ANY TABLE", "Oracle privilege parser should report missing privileges needed by the preview SQL.");
        string oraclePrivilegeCompleteSummary = BuildOraclePrivilegeDiagnosticSummary(
            BuildOraclePrivilegeTable("ALTER", "INDEX"),
            BuildOraclePrivilegeTable("COMMENT ANY TABLE"),
            oracleSql);
        AssertContains(oraclePrivilegeCompleteSummary, "未偵測到明顯缺口", "Oracle privilege parser should recognize when required grants are present.");
        string oracleRepairSuggestions = BuildOracleRepairSuggestions(
            "ORA-01031: insufficient privileges",
            "MAIN",
            "DEMO_TABLE",
            oracleSql + "\nCREATE INDEX \"IX_DEMO_NAME\" ON \"MAIN\".\"DEMO_TABLE\" (\"display_name\");",
            BuildOraclePrivilegeTable("ALTER"),
            BuildOraclePrivilegeTable());
        AssertContains(oracleRepairSuggestions, "SYS_CONTEXT('USERENV','SESSION_USER')", "Oracle repair suggestions should include a session user check.");
        AssertContains(oracleRepairSuggestions, "session_roles", "Oracle repair suggestions should include a role check.");
        AssertContains(oracleRepairSuggestions, "GRANT INDEX ON \"MAIN\".\"DEMO_TABLE\" TO <SESSION_USER>;", "Oracle repair suggestions should include missing object INDEX grant SQL.");
        AssertContains(oracleRepairSuggestions, "GRANT COMMENT ANY TABLE TO <SESSION_USER>;", "Oracle repair suggestions should include missing COMMENT ANY TABLE grant SQL.");
        AssertContains(oracleRepairSuggestions, "跨 schema", "Oracle repair suggestions should call out cross-schema policy.");
        string oracleObjectMissingRepair = BuildOracleRepairSuggestions(
            "ORA-00942: table or view does not exist",
            "MAIN",
            "DEMO_TABLE",
            "ALTER TABLE \"MAIN\".\"DEMO_TABLE\" ADD (\"name\" VARCHAR2(20));",
            BuildOraclePrivilegeTable(),
            BuildOraclePrivilegeTable());
        AssertContains(oracleObjectMissingRepair, "all_objects", "Oracle object missing repair suggestions should include an object existence query.");
        AssertContains(oracleObjectMissingRepair, "GRANT SELECT ON \"MAIN\".\"DEMO_TABLE\" TO <SESSION_USER>;", "Oracle object missing repair suggestions should include SELECT grant SQL.");
        string invalidIdentifierHints = BuildOracleErrorHints("ORA-00904: invalid identifier", "MAIN", "DEMO_TABLE");
        AssertContains(invalidIdentifierHints, "識別名稱無效", "Oracle invalid identifier hints should call out metadata or quoted identifier mismatches.");
        string typeMigrationHints = BuildOracleErrorHints("ORA-01439: column to be modified must be empty to change datatype", "MAIN", "DEMO_TABLE");
        AssertContains(typeMigrationHints, "新增暫存欄位", "Oracle type migration hints should explain staged column migration.");
        string duplicateIndexHints = BuildOracleErrorHints("ORA-01408: such column list already indexed", "MAIN", "DEMO_TABLE");
        AssertContains(duplicateIndexHints, "重複索引", "Oracle duplicate index hints should explain existing column-list indexes.");
        string constraintConflictHints = BuildOracleErrorHints("ORA-02264: name already used by an existing constraint", "MAIN", "DEMO_TABLE");
        AssertContains(constraintConflictHints, "constraint 名稱", "Oracle constraint name hints should explain name conflicts.");
        string quotaHints = BuildOracleErrorHints("ORA-01950: no privileges on tablespace 'USERS'", "MAIN", "DEMO_TABLE");
        AssertContains(quotaHints, "quota", "Oracle quota hints should mention tablespace quota.");
        string oracleQuotaRepair = BuildOracleRepairSuggestions(
            "ORA-01950: no privileges on tablespace 'USERS'",
            "MAIN",
            "DEMO_TABLE",
            "CREATE TABLE \"MAIN\".\"DEMO_TABLE\" (\"ID\" NUMBER);",
            BuildOraclePrivilegeTable(),
            BuildOraclePrivilegeTable());
        AssertContains(oracleQuotaRepair, "DBA 最小授權範本", "Oracle repair suggestions should include a DBA policy template.");
        AssertContains(oracleQuotaRepair, "dba_users", "Oracle quota repair suggestions should include a DBA user/tablespace check.");
        AssertContains(oracleQuotaRepair, "ALTER USER <SESSION_USER> QUOTA <SIZE> ON <TABLESPACE_NAME>;", "Oracle quota repair suggestions should include a quota grant template.");
        AssertEquals("權限查詢結果無法解析：未知錯誤", BuildOracleDiagnosticFailureMessage("Designer.OraclePrivilegeDiagnosticFailed", new Exception("")), "Oracle privilege diagnostic blank errors should localize Traditional Chinese unknown errors.");
        AssertEquals("修復建議無法產生：permission query timeout", BuildOracleDiagnosticFailureMessage("Designer.OracleRepairSuggestionFailed", new InvalidOperationException(" permission query timeout ")), "Oracle repair diagnostic errors should preserve explicit Traditional Chinese reasons.");
        string highRiskOracleMessage = BuildOracleHighRiskConfirmationMessage(
            "ALTER TABLE \"MAIN\".\"DEMO_TABLE\" DROP COLUMN \"legacy_code\";\nDROP INDEX \"MAIN\".\"IX_DEMO\";");
        AssertContains(highRiskOracleMessage, "高風險 Oracle DDL", "Oracle high-risk confirmation should explain the second confirmation.");
        AssertContains(highRiskOracleMessage, "會刪除欄位", "Oracle high-risk confirmation should include drop column warnings.");
        AssertContains(highRiskOracleMessage, "會刪除索引", "Oracle high-risk confirmation should include drop index warnings.");
        string normalOracleMessage = BuildOracleHighRiskConfirmationMessage("COMMENT ON COLUMN \"MAIN\".\"DEMO_TABLE\".\"name\" IS '姓名';");
        AssertEquals("", normalOracleMessage, "Oracle high-risk confirmation should stay empty for non-destructive comments.");
        string oracleDiagnosticLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.English, false);
            AssertEquals("Privilege query result could not be parsed: Unknown error", BuildOracleDiagnosticFailureMessage("Designer.OraclePrivilegeDiagnosticFailed", new Exception("   ")), "Oracle privilege diagnostic blank errors should localize English unknown errors.");
            AssertEquals("Repair suggestions could not be generated: ORA-01031", BuildOracleDiagnosticFailureMessage("Designer.OracleRepairSuggestionFailed", new InvalidOperationException(" ORA-01031 ")), "Oracle repair diagnostic errors should preserve explicit English reasons.");
        }
        finally
        {
            Localization.SetLanguage(oracleDiagnosticLanguage, false);
        }

        string sqliteSql = BuildExistingAlterSql(
            new my_sqlite(),
            "main",
            "demo_table",
            CreateOriginalColumnsForAlter(includeRemovedColumn: true),
            CreateChangedColumnsForAlter(includeRemovedColumn: false),
            "BuildGenericAlterTableSql");
        AssertContains(sqliteSql, "BEGIN TRANSACTION", "SQLite ALTER should rebuild the table inside a transaction.");
        AssertContains(sqliteSql, "CREATE TABLE \"__mysqlpunk_rebuild_demo_table\"", "SQLite ALTER should create a rebuild table.");
        AssertContains(sqliteSql, "DROP TABLE \"demo_table\"", "SQLite ALTER should drop the old table during rebuild.");
        AssertContains(sqliteSql, "RENAME TO \"demo_table\"", "SQLite ALTER should rename the rebuild table back.");
        AssertContains(sqliteSql, "__mysqlpunk_column_comments", "SQLite ALTER should preserve sidecar comments.");

        string oldLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            string sqliteUnsupportedZh = BuildSqliteAlterColumnUnsupportedMessage();
            AssertContains(sqliteUnsupportedZh, "SQLite 修改既有欄位型別", "SQLite unsupported ALTER detail should localize Traditional Chinese messages.");
            AssertContains(sqliteUnsupportedZh, "display_name", "SQLite unsupported ALTER detail should include the column name.");
            DataTable unchangedOriginalZh = CreateOriginalColumnsForAlter(includeRemovedColumn: false);
            string mysqlNoChangesZh = BuildExistingAlterSql(
                new my_mysql(),
                "main",
                "demo_table",
                unchangedOriginalZh,
                unchangedOriginalZh.Copy(),
                "BuildMySqlAlterTableSql");
            AssertEquals("-- 沒有偵測到變更。", mysqlNoChangesZh.Trim(), "MySQL no-change ALTER preview should localize Traditional Chinese messages.");

            Localization.SetLanguage(Localization.English, false);
            string sqliteUnsupportedEn = BuildSqliteAlterColumnUnsupportedMessage();
            AssertContains(sqliteUnsupportedEn, "SQLite requires a table rebuild", "SQLite unsupported ALTER detail should localize English messages.");
            AssertContains(sqliteUnsupportedEn, "display_name", "SQLite unsupported ALTER English detail should include the column name.");
            DataTable unchangedOriginalEn = CreateOriginalColumnsForAlter(includeRemovedColumn: false);
            string mysqlNoChangesEn = BuildExistingAlterSql(
                new my_mysql(),
                "main",
                "demo_table",
                unchangedOriginalEn,
                unchangedOriginalEn.Copy(),
                "BuildMySqlAlterTableSql");
            AssertEquals("-- No changes detected.", mysqlNoChangesEn.Trim(), "MySQL no-change ALTER preview should support English messages.");
        }
        finally
        {
            Localization.SetLanguage(oldLanguage, false);
        }
    }

    private static void TestPreDeleteBackupPathBuilder()
    {
        MethodInfo buildMethod = typeof(Form1).GetMethod("BuildLogicalPreDeleteBackupPath", BindingFlags.Static | BindingFlags.NonPublic);
        string path = (string)buildMethod.Invoke(null, new object[] { "sales db:2026", "mysql", new DateTime(2026, 5, 19, 8, 7, 6) });

        AssertContains(path, "mySQLPunk", "Pre-delete backup path should live under the mySQLPunk backup directory.");
        AssertContains(path, "pre-delete-backups", "Pre-delete backup path should use the pre-delete backup directory.");
        AssertContains(path, "sales_db_2026_mysql_before_delete_20260519_080706.sql", "Pre-delete backup file name should be deterministic and sanitized.");

        MethodInfo restoreBuildMethod = typeof(Form1).GetMethod("BuildPreRestoreBackupPath", BindingFlags.Static | BindingFlags.NonPublic);
        string sqliteRestorePath = (string)restoreBuildMethod.Invoke(null, new object[] { "main/db", "sqlite", new DateTime(2026, 5, 19, 8, 7, 6) });
        string mysqlRestorePath = (string)restoreBuildMethod.Invoke(null, new object[] { "main/db", "mysql", new DateTime(2026, 5, 19, 8, 7, 6) });
        AssertContains(sqliteRestorePath, "pre-restore-backups", "Pre-restore backup path should use the pre-restore backup directory.");
        AssertContains(sqliteRestorePath, "main_db_sqlite_before_restore_20260519_080706.sqlite", "SQLite pre-restore backup should keep a SQLite file extension.");
        AssertContains(mysqlRestorePath, "main_db_mysql_before_restore_20260519_080706.sql", "Logical pre-restore backup should use SQL extension.");
    }

    private static void TestPreDeleteBackupArchiveService()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mysqlpunk_predelete_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string sqlPath = Path.Combine(dir, "main_mysql_before_delete_20260519_080706.sql");
            File.WriteAllText(sqlPath, "SELECT 1;", Encoding.UTF8);
            string archivePath = PreDeleteBackupArchiveService.ArchiveBackupFile(sqlPath);

            Assert(!File.Exists(sqlPath), "Archive service should remove the uncompressed backup after zip succeeds.");
            Assert(File.Exists(archivePath), "Archive service should create a zip file.");
            Assert(Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase), "Archive path should use zip extension.");

            byte[] archiveBytes = File.ReadAllBytes(archivePath);
            Assert(archiveBytes.Length > 4 && archiveBytes[0] == 0x50 && archiveBytes[1] == 0x4B, "Archive should have a zip header.");
            string archiveText = Encoding.UTF8.GetString(archiveBytes);
            AssertContains(archiveText, "main_mysql_before_delete_20260519_080706.sql", "Archive should keep the original backup file name as entry.");

            for (int i = 0; i < 5; i++)
            {
                string oldPath = Path.Combine(dir, "old_" + i + "_before_delete_20260519_08070" + i + ".zip");
                File.WriteAllText(oldPath, "zip-placeholder");
                File.SetLastWriteTimeUtc(oldPath, new DateTime(2026, 5, 19, 8, 0, i, DateTimeKind.Utc));
            }

            int deleted = PreDeleteBackupArchiveService.PruneOldArchives(dir, PreDeleteBackupArchiveService.BackupArchivePattern, 3);
            int remaining = Directory.GetFiles(dir, PreDeleteBackupArchiveService.BackupArchivePattern).Length;
            Assert(deleted >= 3, "Prune service should delete archives beyond retention count.");
            Assert(remaining == 3, "Prune service should keep only the retention count.");

            string restoreSqlPath = Path.Combine(dir, "main_mysql_before_restore_20260519_080706.sql");
            File.WriteAllText(restoreSqlPath, "SELECT 2;", Encoding.UTF8);
            string restoreArchivePath = PreDeleteBackupArchiveService.ArchiveAndPrune(
                restoreSqlPath,
                2,
                PreDeleteBackupArchiveService.PreRestoreBackupArchivePattern);
            Assert(File.Exists(restoreArchivePath), "Archive service should create pre-restore zip archives.");
            Assert(Path.GetFileName(restoreArchivePath).Contains("before_restore"), "Pre-restore archive should keep the restore marker.");

            string remoteDir = Path.Combine(dir, "remote");
            string mirrorPath = BackupRemoteMirrorService.MirrorBackup(restoreArchivePath, remoteDir);
            Assert(File.Exists(mirrorPath), "Remote mirror service should copy backup files.");
            AssertEquals(Path.GetFileName(restoreArchivePath), Path.GetFileName(mirrorPath), "Remote mirror should preserve the backup file name.");

            string secondMirrorPath = BackupRemoteMirrorService.MirrorBackup(restoreArchivePath, remoteDir);
            Assert(File.Exists(secondMirrorPath), "Remote mirror service should avoid overwriting existing copies.");
            Assert(!string.Equals(mirrorPath, secondMirrorPath, StringComparison.OrdinalIgnoreCase), "Remote mirror duplicate should receive a unique file name.");

            for (int i = 0; i < 4; i++)
            {
                string managedPath = Path.Combine(remoteDir, "main_backup_20260519_08070" + i + ".sql");
                File.WriteAllText(managedPath, "remote-placeholder");
                File.SetLastWriteTimeUtc(managedPath, new DateTime(2026, 5, 19, 8, 10, i, DateTimeKind.Utc));
            }
            string unrelatedPath = Path.Combine(remoteDir, "keep_me.txt");
            File.WriteAllText(unrelatedPath, "not a backup");

            int pruned = BackupRemoteMirrorService.PruneRemoteBackups(remoteDir, 2);
            int remoteManagedCount = Directory.GetFiles(remoteDir, "*_backup_*.sql").Length;
            Assert(pruned >= 2, "Remote mirror retention should prune old managed backup files.");
            Assert(remoteManagedCount == 2, "Remote mirror retention should keep configured managed backup count.");
            Assert(File.Exists(unrelatedPath), "Remote mirror retention should not delete unrelated files.");

            string oldLanguage = Localization.CurrentLanguage;
            try
            {
                Localization.SetLanguage(Localization.TraditionalChinese, false);
                try
                {
                    PreDeleteBackupArchiveService.ArchiveBackupFile("");
                    Assert(false, "Pre-delete backup archive should require a source path.");
                }
                catch (ArgumentException ex)
                {
                    AssertContains(ex.Message, "請指定備份來源路徑", "Pre-delete archive source path validation should localize Traditional Chinese messages.");
                }

                try
                {
                    PreDeleteBackupArchiveService.ArchiveBackupFile(Path.Combine(dir, "missing.sql"));
                    Assert(false, "Missing pre-delete backup source should throw.");
                }
                catch (FileNotFoundException ex)
                {
                    AssertContains(ex.Message, "找不到備份檔案", "Pre-delete archive should localize Traditional Chinese missing file errors.");
                }

                Localization.SetLanguage(Localization.English, false);
                try
                {
                    BackupRemoteMirrorService.MirrorBackup("", remoteDir);
                    Assert(false, "Remote mirror should require a source path.");
                }
                catch (ArgumentException ex)
                {
                    AssertContains(ex.Message, "Backup source path is required", "Remote mirror source path validation should localize English messages.");
                }

                try
                {
                    BackupRemoteMirrorService.MirrorBackup(Path.Combine(dir, "missing.sql"), remoteDir);
                    Assert(false, "Missing remote mirror source should throw.");
                }
                catch (FileNotFoundException ex)
                {
                    AssertContains(ex.Message, "Backup file not found", "Remote mirror should localize English missing file errors.");
                }
            }
            finally
            {
                Localization.SetLanguage(oldLanguage, false);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static void TestBackupRestoreService()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mysqlpunk_restore_" + Guid.NewGuid().ToString("N"));
        int oldRestoreContentSnapshotMaxRows = BackupMirrorSettings.RestoreContentSnapshotMaxRows;
        Directory.CreateDirectory(dir);
        try
        {
            string sql = "CREATE TABLE a (id int);\nINSERT INTO a VALUES (1);\n";
            string sqlPath = Path.Combine(dir, "backup.sql");
            File.WriteAllText(sqlPath, sql, Encoding.UTF8);

            BackupRestorePackage sqlPackage = BackupRestoreService.LoadRestorePackage(
                sqlPath,
                script => (int)typeof(Form1)
                    .GetMethod("CountSqlScriptStatements", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { script }));

            Assert(!sqlPackage.IsZip, "SQL restore package should not be marked as zip.");
            Assert(sqlPackage.StatementCount == 2, "SQL restore package should count executable statements.");
            AssertContains(sqlPackage.Script, "CREATE TABLE a", "SQL restore package should read script content.");

            string zipSourcePath = Path.Combine(dir, "restore.sql");
            File.WriteAllText(zipSourcePath, sql, Encoding.UTF8);
            string zipPath = PreDeleteBackupArchiveService.ArchiveBackupFile(zipSourcePath);

            BackupRestorePackage zipPackage = BackupRestoreService.LoadRestorePackage(zipPath, script => 7);
            Assert(zipPackage.IsZip, "Zip restore package should be marked as zip.");
            AssertEquals("restore.sql", zipPackage.EntryName, "Zip restore package should expose selected SQL entry name.");
            Assert(zipPackage.StatementCount == 7, "Zip restore package should use provided statement counter.");
            AssertContains(zipPackage.Script, "INSERT INTO a", "Zip restore package should read SQL entry content.");

            Func<string, int> countSqlStatements = script => (int)typeof(Form1)
                .GetMethod("CountSqlScriptStatements", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { script });

            BackupIntegrityResult sqlIntegrity = BackupIntegrityService.VerifyBackup(sqlPath, countSqlStatements);
            Assert(sqlIntegrity.IsValid && sqlIntegrity.StatementCount == 2, "SQL backup integrity should validate executable statements.");

            BackupIntegrityResult zipIntegrity = BackupIntegrityService.VerifyBackup(zipPath, script => 2);
            Assert(zipIntegrity.IsValid && zipIntegrity.Kind == "sql", "Zip SQL backup integrity should validate the SQL entry.");

            string sqlitePath = Path.Combine(dir, "verify.sqlite");
            using (my_sqlite sqliteDb = new my_sqlite())
            {
                sqliteDb.SetConn("Data Source=" + sqlitePath + ";Version=3;New=True;");
                sqliteDb.Open();
                Dictionary<string, string> createSqliteResult = sqliteDb.ExecSQL("CREATE TABLE ok_test (id INTEGER PRIMARY KEY);");
                AssertEquals("OK", createSqliteResult["status"], "SQLite integrity test database should be created.");
            }
            BackupIntegrityResult sqliteIntegrity = BackupIntegrityService.VerifyBackup(sqlitePath, null);
            Assert(sqliteIntegrity.IsValid && sqliteIntegrity.Kind == "sqlite", "SQLite backup integrity should run integrity_check.");

            string emptySqlPath = Path.Combine(dir, "empty.sql");
            File.WriteAllText(emptySqlPath, string.Empty, Encoding.UTF8);
            BackupIntegrityResult emptyIntegrity = BackupIntegrityService.VerifyBackup(emptySqlPath, countSqlStatements);
            Assert(!emptyIntegrity.IsValid, "Empty SQL backup should fail integrity verification.");
            string oldLanguage = Localization.CurrentLanguage;
            try
            {
                Localization.SetLanguage(Localization.TraditionalChinese, false);
                BackupIntegrityResult emptyPathIntegrity = BackupIntegrityService.VerifyBackup("", countSqlStatements);
                AssertContains(emptyPathIntegrity.Message, "空", "Empty backup path should return a Traditional Chinese validation message.");

                string unsupportedPath = Path.Combine(dir, "backup.txt");
                File.WriteAllText(unsupportedPath, "not a supported backup", Encoding.UTF8);
                BackupIntegrityResult unsupportedZh = BackupIntegrityService.VerifyBackup(unsupportedPath, countSqlStatements);
                AssertContains(unsupportedZh.Message, "不支援", "Unsupported backup type should return a Traditional Chinese validation message.");

                try
                {
                    BackupIntegrityScheduleService.QuarantineFailedBackups(new BackupIntegrityScheduleReport(), "");
                    Assert(false, "Backup quarantine should require a quarantine directory.");
                }
                catch (ArgumentException ex)
                {
                    AssertContains(ex.Message, "請指定備份隔離資料夾", "Backup quarantine directory validation should localize Traditional Chinese messages.");
                }

                Localization.SetLanguage(Localization.English, false);
                BackupIntegrityResult missingIntegrity = BackupIntegrityService.VerifyBackup(Path.Combine(dir, "missing.sql"), countSqlStatements);
                AssertContains(missingIntegrity.Message, "does not exist", "Missing backup file should return an English validation message.");
                BackupIntegrityResult unsupportedEn = BackupIntegrityService.VerifyBackup(unsupportedPath, countSqlStatements);
                AssertContains(unsupportedEn.Message, ".txt", "Unsupported backup type should include the rejected extension.");

                try
                {
                    BackupRestoreService.LoadRestorePackage("", countSqlStatements);
                    Assert(false, "Restore package should require a source path.");
                }
                catch (ArgumentException ex)
                {
                    AssertContains(ex.Message, "Backup source path is required", "Restore package source path validation should localize English messages.");
                }

                try
                {
                    BackupIntegrityScheduleService.WriteReport(new BackupIntegrityScheduleReport(), "");
                    Assert(false, "Backup integrity report should require a report directory.");
                }
                catch (ArgumentException ex)
                {
                    AssertContains(ex.Message, "Backup integrity report directory is required", "Backup integrity report directory validation should localize English messages.");
                }

                try
                {
                    BackupRestoreService.LoadRestorePackage(Path.Combine(dir, "missing-restore.sql"), countSqlStatements);
                    Assert(false, "Missing restore package should throw.");
                }
                catch (FileNotFoundException ex)
                {
                    AssertContains(ex.Message, "Backup file not found", "Restore package should localize English missing file errors.");
                }

                try
                {
                    BackupRestoreService.LoadRestorePackage(emptySqlPath, countSqlStatements);
                    Assert(false, "Empty restore package should throw.");
                }
                catch (InvalidOperationException ex)
                {
                    AssertContains(ex.Message, "does not contain executable SQL", "Restore package should localize English empty SQL errors.");
                }

                MethodInfo createSqlitePreDeleteBackupMethod = typeof(Form1).GetMethod("CreateSqlitePreDeleteBackup", BindingFlags.Static | BindingFlags.NonPublic);
                try
                {
                    createSqlitePreDeleteBackupMethod.Invoke(null, new object[] { new my_sqlite(), sqlitePath, "" });
                    Assert(false, "SQLite pre-delete backup should require an output path.");
                }
                catch (TargetInvocationException ex)
                {
                    ArgumentException argumentException = ex.InnerException as ArgumentException;
                    Assert(argumentException != null, "SQLite pre-delete backup should throw ArgumentException for missing output paths.");
                    AssertContains(argumentException.Message, "Backup output path is required", "SQLite pre-delete backup output path validation should localize English messages.");
                }

                string noSqlZipPath = Path.Combine(dir, "no-sql.zip");
                using (ZipArchive archive = ZipFile.Open(noSqlZipPath, ZipArchiveMode.Create))
                {
                    ZipArchiveEntry entry = archive.CreateEntry("notes.txt");
                    using (StreamWriter writer = new StreamWriter(entry.Open(), Encoding.UTF8))
                    {
                        writer.Write("not sql");
                    }
                }
                try
                {
                    BackupRestoreService.LoadRestorePackage(noSqlZipPath, countSqlStatements);
                    Assert(false, "Zip restore package without SQL should throw.");
                }
                catch (InvalidOperationException ex)
                {
                    AssertContains(ex.Message, "does not contain a .sql entry", "Restore package should localize English zip-without-SQL errors.");
                }
            }
            finally
            {
                Localization.SetLanguage(oldLanguage, false);
            }
            string batchEmptySqlPath = Path.Combine(dir, "batch-empty.sql");
            File.WriteAllText(batchEmptySqlPath, string.Empty, Encoding.UTF8);

            Assert(BackupIntegrityScheduleService.IsDue(true, DateTime.MinValue, 24, DateTime.UtcNow), "Backup integrity schedule should run when never verified.");
            Assert(!BackupIntegrityScheduleService.IsDue(true, DateTime.UtcNow.AddHours(-2), 24, DateTime.UtcNow), "Backup integrity schedule should wait for the configured interval.");
            Assert(!BackupIntegrityScheduleService.IsDue(false, DateTime.MinValue, 24, DateTime.UtcNow), "Disabled backup integrity schedule should not run.");

            string blankFailureDirectory = Path.Combine(dir, "blank-failure");
            Directory.CreateDirectory(blankFailureDirectory);
            string blankFailureSqlPath = Path.Combine(blankFailureDirectory, "blank-failure.sql");
            File.WriteAllText(blankFailureSqlPath, "SELECT 1;", Encoding.UTF8);
            oldLanguage = Localization.CurrentLanguage;
            try
            {
                Localization.SetLanguage(Localization.TraditionalChinese, false);
                Func<string, int> blankFailureCounter = script => { throw new Exception(""); };
                BackupIntegrityScheduleReport blankFailureReport = BackupIntegrityScheduleService.VerifyDirectories(new[] { blankFailureDirectory }, blankFailureCounter, 10);
                Assert(blankFailureReport.FailedFiles == 1, "Backup integrity schedule should report callback exceptions as failures.");
                AssertEquals("未知錯誤", blankFailureReport.FailedResults[0].Message, "Backup integrity schedule blank callback errors should localize Traditional Chinese unknown errors.");
                BackupQuarantineRestorePreview blankFailurePreview = BackupQuarantineRestoreService.BuildPreview(
                    new BackupQuarantineRestoreCandidate
                    {
                        QuarantinedPath = blankFailureSqlPath,
                        OriginalPath = "",
                        SizeBytes = new FileInfo(blankFailureSqlPath).Length,
                        QuarantinedAtUtc = DateTime.UtcNow
                    },
                    blankFailureCounter);
                AssertEquals("未知錯誤", blankFailurePreview.IntegrityResult.Message, "Quarantine restore preview blank verification errors should localize Traditional Chinese unknown errors.");

                Localization.SetLanguage(Localization.English, false);
                BackupIntegrityScheduleReport englishBlankFailureReport = BackupIntegrityScheduleService.VerifyDirectories(new[] { blankFailureDirectory }, blankFailureCounter, 10);
                AssertEquals("Unknown error", englishBlankFailureReport.FailedResults[0].Message, "Backup integrity schedule blank callback errors should localize English unknown errors.");
            }
            finally
            {
                Localization.SetLanguage(oldLanguage, false);
            }

            BackupIntegrityScheduleReport scheduleReport = BackupIntegrityScheduleService.VerifyDirectories(new[] { dir }, countSqlStatements, 10);
            Assert(scheduleReport.TotalFiles >= 4, "Backup integrity schedule should scan supported backup files.");
            Assert(scheduleReport.VerifiedFiles >= 3, "Backup integrity schedule should verify readable SQL, ZIP, and SQLite backups.");
            Assert(scheduleReport.FailedFiles >= 1, "Backup integrity schedule should report invalid backups.");
            string quarantineDirectory = Path.Combine(dir, "quarantine");
            BackupIntegrityQuarantineResult quarantineResult = BackupIntegrityScheduleService.QuarantineFailedBackups(scheduleReport, quarantineDirectory);
            Assert(quarantineResult.MovedFiles >= 2, "Backup integrity quarantine should move invalid backups.");
            Assert(quarantineResult.MovedPaths.Count >= 1 && File.Exists(quarantineResult.MovedPaths[0]), "Quarantined backup file should exist in quarantine folder.");
            Assert(quarantineResult.Entries.Count >= 1, "Backup integrity quarantine should remember original paths.");
            Assert(File.Exists(quarantineResult.ManifestPath), "Backup integrity quarantine should write a manifest file.");
            Assert(!File.Exists(emptySqlPath), "Invalid backup should be moved out of the original folder.");
            BackupQuarantineRestoreCandidate candidate = null;
            foreach (BackupQuarantineRestoreCandidate item in BackupQuarantineRestoreService.FindCandidates(quarantineDirectory))
            {
                if (string.Equals(item.OriginalPath, emptySqlPath, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = item;
                    break;
                }
            }
            Assert(candidate != null && candidate.HasOriginalPath, "Quarantine restore should find the original path from the manifest.");
            AssertEquals(emptySqlPath, candidate.OriginalPath, "Quarantine restore candidate should keep the original backup path.");
            BackupQuarantineRestorePreview preview = BackupQuarantineRestoreService.BuildPreview(candidate, countSqlStatements);
            Assert(!preview.PassedIntegrityCheck, "Quarantine restore preview should rerun integrity verification before restore.");
            Assert(preview.IntegrityResult.Message.IndexOf("empty", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   preview.IntegrityResult.Message.Contains("空"),
                "Quarantine restore preview should include the integrity failure reason.");
            AssertContains(preview.DestinationDiffSummary, "原始路徑目前不存在", "Quarantine restore preview should show destination diff for missing original path.");
            BackupQuarantineRestoreResult restoreResult = BackupQuarantineRestoreService.RestoreQuarantinedFile(candidate.QuarantinedPath, candidate.OriginalPath, false);
            Assert(File.Exists(restoreResult.RestoredPath), "Quarantined backup should be restored to the original folder.");
            Assert(!File.Exists(candidate.QuarantinedPath), "Quarantined backup should be moved out of quarantine after restore.");
            File.WriteAllText(restoreResult.RestoredPath, "SELECT 1;", Encoding.UTF8);
            BackupQuarantineRestoreCandidate changedCandidate = new BackupQuarantineRestoreCandidate
            {
                QuarantinedPath = batchEmptySqlPath,
                OriginalPath = restoreResult.RestoredPath,
                SizeBytes = 0,
                QuarantinedAtUtc = DateTime.UtcNow
            };
            string diffSummary = BackupQuarantineRestoreService.BuildDestinationDiffSummary(changedCandidate);
            AssertContains(diffSummary, "大小", "Quarantine restore preview should compare destination file sizes.");
            string unsupportedQuarantinePath = Path.Combine(quarantineDirectory, "unsupported.txt");
            File.WriteAllText(unsupportedQuarantinePath, "not a backup", Encoding.UTF8);
            oldLanguage = Localization.CurrentLanguage;
            try
            {
                Localization.SetLanguage(Localization.TraditionalChinese, false);
                try
                {
                    BackupQuarantineRestoreService.RestoreQuarantinedFile(unsupportedQuarantinePath, Path.Combine(dir, "unsupported.txt"), false);
                    Assert(false, "Unsupported quarantined backup type should fail before restore.");
                }
                catch (InvalidOperationException ex)
                {
                    AssertContains(ex.Message, "不支援", "Unsupported quarantined backup type should return a Traditional Chinese validation message.");
                    AssertContains(ex.Message, ".txt", "Unsupported quarantined backup type should include the rejected extension.");
                }

                Localization.SetLanguage(Localization.English, false);
                string englishNoManifestDiff = BackupQuarantineRestoreService.BuildDestinationDiffSummary(
                    new BackupQuarantineRestoreCandidate
                    {
                        QuarantinedPath = unsupportedQuarantinePath,
                        OriginalPath = "",
                        SizeBytes = new FileInfo(unsupportedQuarantinePath).Length,
                        QuarantinedAtUtc = DateTime.UtcNow
                    });
                AssertContains(englishNoManifestDiff, "no original path", "Quarantine restore destination diff should support English.");
            }
            finally
            {
                Localization.SetLanguage(oldLanguage, false);
            }
            string orphanQuarantinePath = Path.Combine(quarantineDirectory, "20240201_120000_orphan.sql");
            File.WriteAllText(orphanQuarantinePath, "SELECT 1;", Encoding.UTF8);
            BackupQuarantineBatchRestoreResult batchRestore = BackupQuarantineRestoreService.RestoreAllToOriginalPaths(
                BackupQuarantineRestoreService.FindCandidates(quarantineDirectory),
                false);
            Assert(batchRestore.RestoredFiles >= 1, "Batch quarantine restore should move remaining files with original paths.");
            Assert(batchRestore.SkippedNoOriginalPath >= 1, "Batch quarantine restore should skip quarantined files without original paths.");
            Assert(File.Exists(batchEmptySqlPath), "Batch quarantine restore should restore files to their original paths.");
            for (int i = 0; i < 5; i++)
            {
                string oldQuarantine = Path.Combine(quarantineDirectory, "2024010" + (i + 1) + "_120000_old_" + i + ".sql");
                File.WriteAllText(oldQuarantine, "SELECT " + i + ";", Encoding.UTF8);
                File.SetLastWriteTimeUtc(oldQuarantine, DateTime.UtcNow.AddDays(-10 - i));
            }
            int prunedQuarantine = BackupIntegrityScheduleService.PruneQuarantine(quarantineDirectory, 2);
            Assert(prunedQuarantine >= 4, "Backup integrity quarantine prune should remove old managed files beyond retention.");
            string reportDirectory = Path.Combine(dir, "reports");
            string reportPath = BackupIntegrityScheduleService.WriteReport(scheduleReport, reportDirectory);
            Assert(File.Exists(reportPath), "Backup integrity schedule should write a report file.");
            JObject reportJson = JObject.Parse(File.ReadAllText(reportPath, Encoding.UTF8));
            Assert((int)reportJson["FailedFiles"] >= 1, "Backup integrity report should include failed file count.");
            Assert(reportJson["FailedResults"] != null && reportJson["FailedResults"].HasValues, "Backup integrity report should include failed result details.");
            Assert(reportJson["QuarantineResult"] != null && (int)reportJson["QuarantineResult"]["MovedFiles"] >= 1, "Backup integrity report should include quarantine result.");

            DatabaseRestoreSnapshot before = BackupRestoreDiffService.CreateSnapshot("main", "sqlite", 2, 1, 0, 1);
            DatabaseRestoreSnapshot after = BackupRestoreDiffService.CreateSnapshot("main", "sqlite", 3, 1, 1, 0);
            string summary = BackupRestoreDiffService.BuildSummary(before, after);
            AssertContains(summary, "資料表：2 -> 3 (+1)", "Restore diff should show added tables.");
            AssertContains(summary, "檢視：1 -> 1 (0)", "Restore diff should show unchanged views.");
            AssertContains(summary, "函式/程序：0 -> 1 (+1)", "Restore diff should show added routines.");
            AssertContains(summary, "事件/Trigger：1 -> 0 (-1)", "Restore diff should show removed events.");
            DatabaseRestoreSnapshot namedBefore = BackupRestoreDiffService.CreateSnapshot(
                "main",
                "sqlite",
                new[] { "users", "old_logs" },
                new[] { "active_users" },
                new string[0],
                new[] { "ev_old" });
            DatabaseRestoreSnapshot namedAfter = BackupRestoreDiffService.CreateSnapshot(
                "main",
                "sqlite",
                new[] { "users", "orders", "audit_log", "archive_2026", "daily_stats", "monthly_stats", "yearly_stats" },
                new[] { "active_users", "order_view" },
                new[] { "fn_refresh" },
                new string[0]);
            string namedSummary = BackupRestoreDiffService.BuildSummary(namedBefore, namedAfter);
            AssertContains(namedSummary, "資料表：2 -> 7 (+5)", "Named restore diff should still show table count delta.");
            AssertContains(namedSummary, "新增：archive_2026, audit_log, daily_stats, monthly_stats, orders ... 等 6 個", "Named restore diff should summarize long added table lists.");
            AssertContains(namedSummary, "移除：old_logs", "Named restore diff should show removed table names.");
            AssertContains(namedSummary, "檢視：1 -> 2 (+1)，新增：order_view", "Named restore diff should show added views.");
            AssertContains(namedSummary, "函式/程序：0 -> 1 (+1)，新增：fn_refresh", "Named restore diff should show added routines.");
            AssertContains(namedSummary, "事件/Trigger：1 -> 0 (-1)，移除：ev_old", "Named restore diff should show removed events.");

            DatabaseRestoreSnapshot schemaBefore = BackupRestoreDiffService.CreateSnapshot(
                "main",
                "sqlite",
                new[] { "users" },
                new string[0],
                new string[0],
                new string[0]);
            DatabaseRestoreSnapshot schemaAfter = BackupRestoreDiffService.CreateSnapshot(
                "main",
                "sqlite",
                new[] { "users" },
                new string[0],
                new string[0],
                new string[0]);
            BackupRestoreDiffService.AddTableColumns(schemaBefore, "users", BackupRestoreDiffService.CreateColumnSnapshots("users", BuildRestoreColumnMetadata(new[]
            {
                new[] { "id", "INTEGER", "NO", "", "識別碼", "1" },
                new[] { "name", "varchar(50)", "NO", "''", "姓名", "2" },
                new[] { "legacy_code", "text", "YES", "", "", "3" }
            })));
            BackupRestoreDiffService.AddTableColumns(schemaAfter, "users", BackupRestoreDiffService.CreateColumnSnapshots("users", BuildRestoreColumnMetadata(new[]
            {
                new[] { "id", "INTEGER", "NO", "", "識別碼", "1" },
                new[] { "name", "varchar(100)", "YES", "NULL", "顯示名稱", "2" },
                new[] { "email", "text", "YES", "", "Email", "3" }
            })));
            BackupRestoreDiffService.SetTableRowCount(schemaBefore, "users", 2);
            BackupRestoreDiffService.SetTableRowCount(schemaAfter, "users", 5);
            string schemaSummary = BackupRestoreDiffService.BuildSummary(schemaBefore, schemaAfter);
            AssertContains(schemaSummary, "資料列差異：users：2 -> 5 (+3)", "Restore diff should include row count changes.");
            AssertContains(schemaSummary, "欄位差異：", "Restore diff should include schema column details.");
            AssertContains(schemaSummary, "新增 users.email", "Restore diff should include added columns.");
            AssertContains(schemaSummary, "移除 users.legacy_code", "Restore diff should include removed columns.");
            AssertContains(schemaSummary, "變更 users.name", "Restore diff should include changed columns.");
            AssertContains(schemaSummary, "型別：varchar(50) -> varchar(100)", "Restore diff should include type changes.");
            AssertContains(schemaSummary, "NULL：NO -> YES", "Restore diff should include nullability changes.");
            AssertContains(schemaSummary, "預設：'' -> NULL", "Restore diff should include default changes.");
            AssertContains(schemaSummary, "註解：姓名 -> 顯示名稱", "Restore diff should include comment changes.");

            DatabaseRestoreSnapshot contentBefore = BackupRestoreDiffService.CreateSnapshot(
                "main",
                "sqlite",
                new[] { "users", "orders" },
                new string[0],
                new string[0],
                new string[0]);
            DatabaseRestoreSnapshot contentAfter = BackupRestoreDiffService.CreateSnapshot(
                "main",
                "sqlite",
                new[] { "users", "orders" },
                new string[0],
                new string[0],
                new string[0]);
            BackupRestoreDiffService.SetTableContentFingerprint(contentBefore, "users", 2, BuildRestoreContentRows(new[]
            {
                new object[] { 1, "Alice", new byte[] { 1, 2 } },
                new object[] { 2, "Bob", DBNull.Value }
            }));
            BackupRestoreDiffService.SetTableContentFingerprint(contentAfter, "users", 2, BuildRestoreContentRows(new[]
            {
                new object[] { 2, "Bob", DBNull.Value },
                new object[] { 1, "Alice", new byte[] { 1, 2 } }
            }));
            BackupRestoreDiffService.SetTableContentFingerprint(contentBefore, "orders", 2, BuildRestoreContentRows(new[]
            {
                new object[] { 10, "pending", null },
                new object[] { 11, "paid", null }
            }));
            BackupRestoreDiffService.SetTableContentFingerprint(contentAfter, "orders", 2, BuildRestoreContentRows(new[]
            {
                new object[] { 10, "pending", null },
                new object[] { 11, "refunded", null }
            }));
            string contentSummary = BackupRestoreDiffService.BuildSummary(contentBefore, contentAfter);
            Assert(!contentSummary.Contains("users：內容指紋變更"), "Restore content diff should be row-order independent.");
            AssertContains(contentSummary, "資料內容差異：orders：內容指紋變更", "Restore content diff should detect changed row values.");
            AssertContains(contentSummary, "比對 2/2 列", "Restore content diff should report full comparison coverage for small tables.");
            AssertContains(contentSummary, "列數 2 -> 2", "Restore content diff should include row counts for same-count content changes.");

            int beforePageLoads = 0;
            DatabaseRestoreTableContentSnapshot largeBeforeContent = BackupRestoreDiffService.CreateTableContentFingerprint(
                "large_orders",
                250,
                (offset, limit) =>
                {
                    beforePageLoads++;
                    return BuildPagedRestoreContentRows(offset, limit, -1);
                },
                40,
                120);
            int afterPageLoads = 0;
            DatabaseRestoreTableContentSnapshot largeAfterContent = BackupRestoreDiffService.CreateTableContentFingerprint(
                "large_orders",
                250,
                (offset, limit) =>
                {
                    afterPageLoads++;
                    return BuildPagedRestoreContentRows(offset, limit, 80);
                },
                40,
                120);
            Assert(beforePageLoads > 1 && afterPageLoads > 1, "Large restore content fingerprints should load data in multiple pages.");
            Assert(largeBeforeContent.SampledRows == 120 && largeBeforeContent.IsPartial, "Large restore content fingerprint should cap sampled rows and mark partial coverage.");
            DatabaseRestoreSnapshot largeBefore = BackupRestoreDiffService.CreateSnapshot("main", "sqlite", new[] { "large_orders" }, new string[0], new string[0], new string[0]);
            DatabaseRestoreSnapshot largeAfter = BackupRestoreDiffService.CreateSnapshot("main", "sqlite", new[] { "large_orders" }, new string[0], new string[0], new string[0]);
            BackupRestoreDiffService.SetTableContentFingerprint(largeBefore, "large_orders", largeBeforeContent);
            BackupRestoreDiffService.SetTableContentFingerprint(largeAfter, "large_orders", largeAfterContent);
            string largeContentSummary = BackupRestoreDiffService.BuildSummary(largeBefore, largeAfter);
            AssertContains(largeContentSummary, "large_orders：內容指紋變更", "Large restore content diff should detect sampled row value changes.");
            AssertContains(largeContentSummary, "抽樣 120/250 列", "Large restore content diff should report sampled coverage.");

            string restoreDiffLanguage = Localization.CurrentLanguage;
            try
            {
                Localization.SetLanguage(Localization.English, false);
                string englishNamedSummary = BackupRestoreDiffService.BuildSummary(namedBefore, namedAfter);
                AssertContains(englishNamedSummary, "Tables: 2 -> 7 (+5)", "Restore diff should localize English table count lines.");
                AssertContains(englishNamedSummary, "Added: archive_2026, audit_log, daily_stats, monthly_stats, orders ... and 6 items", "Restore diff should localize English long added table lists.");
                AssertContains(englishNamedSummary, "Removed: old_logs", "Restore diff should localize English removed table names.");
                AssertContains(englishNamedSummary, "Views: 1 -> 2 (+1)", "Restore diff should localize English view count lines.");

                string englishSchemaSummary = BackupRestoreDiffService.BuildSummary(schemaBefore, schemaAfter);
                AssertContains(englishSchemaSummary, "Row count diff: users: 2 -> 5 (+3)", "Restore diff should localize English row count changes.");
                AssertContains(englishSchemaSummary, "Column diff:", "Restore diff should localize English schema column headings.");
                AssertContains(englishSchemaSummary, "Added users.email", "Restore diff should localize English added columns.");
                AssertContains(englishSchemaSummary, "Removed users.legacy_code", "Restore diff should localize English removed columns.");
                AssertContains(englishSchemaSummary, "Changed users.name", "Restore diff should localize English changed columns.");
                AssertContains(englishSchemaSummary, "type: varchar(50) -> varchar(100)", "Restore diff should localize English type changes.");
                AssertContains(englishSchemaSummary, "default: '' -> NULL", "Restore diff should localize English default changes.");
                AssertContains(englishSchemaSummary, "comment: 姓名 -> 顯示名稱", "Restore diff should localize English comment change labels.");

                string englishContentSummary = BackupRestoreDiffService.BuildSummary(contentBefore, contentAfter);
                Assert(!englishContentSummary.Contains("users: content fingerprint changed"), "English restore content diff should remain row-order independent.");
                AssertContains(englishContentSummary, "Data content diff: orders: content fingerprint changed", "Restore content diff should localize English content changes.");
                AssertContains(englishContentSummary, "compared 2/2 rows", "Restore content diff should localize English full comparison coverage.");
                AssertContains(englishContentSummary, "rows 2 -> 2", "Restore content diff should localize English row counts.");

                string englishLargeContentSummary = BackupRestoreDiffService.BuildSummary(largeBefore, largeAfter);
                AssertContains(englishLargeContentSummary, "large_orders: content fingerprint changed", "Large restore content diff should localize English changed table details.");
                AssertContains(englishLargeContentSummary, "sampled 120/250 rows", "Large restore content diff should localize English sampled coverage.");
            }
            finally
            {
                Localization.SetLanguage(restoreDiffLanguage, false);
            }

            DatabaseRestoreContentScanReport scanReport = BackupRestoreDiffService.BuildContentScanReport(
                largeBefore,
                largeAfter,
                new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            Assert(scanReport.Summary.TotalTables == 1, "Restore content scan report should include scanned table totals.");
            Assert(scanReport.Summary.ChangedTables == 1, "Restore content scan report should count changed tables.");
            Assert(scanReport.Summary.PartialTables == 1, "Restore content scan report should count partial table scans.");
            Assert(scanReport.Summary.BeforeSampledRows == 120 && scanReport.Summary.AfterSampledRows == 120, "Restore content scan report should include sampled row totals.");
            Assert(scanReport.Tables.Count == 1 && scanReport.Tables[0].IsChanged, "Restore content scan report should flag changed table details.");
            Assert(scanReport.Tables[0].BeforeFingerprintShort.Length == 12, "Restore content scan report should include short fingerprints.");

            string restoreReportDirectory = Path.Combine(dir, "restore-content-reports");
            string restoreReportPath = BackupRestoreDiffService.WriteContentScanReport(largeBefore, largeAfter, restoreReportDirectory);
            Assert(File.Exists(restoreReportPath), "Restore content scan report should be written to disk.");
            JObject restoreReportJson = JObject.Parse(File.ReadAllText(restoreReportPath, Encoding.UTF8));
            Assert((int)restoreReportJson["Summary"]["ChangedTables"] == 1, "Restore content scan JSON should include changed table count.");
            Assert((bool)restoreReportJson["Tables"][0]["IsChanged"], "Restore content scan JSON should include table change details.");
            Assert((int)restoreReportJson["Tables"][0]["BeforeSampledRows"] == 120, "Restore content scan JSON should include sampled row coverage.");

            oldLanguage = Localization.CurrentLanguage;
            try
            {
                Localization.SetLanguage(Localization.English, false);
                try
                {
                    BackupRestoreDiffService.WriteContentScanReport(largeBefore, largeAfter, "");
                    Assert(false, "Restore content scan report should require a report directory.");
                }
                catch (ArgumentException ex)
                {
                    AssertContains(ex.Message, "Restore content scan report directory is required", "Restore content scan report directory validation should localize English messages.");
                }
            }
            finally
            {
                Localization.SetLanguage(oldLanguage, false);
            }

            Assert(BackupRestoreDiffService.ResolveMaxContentSnapshotRows(0) == BackupRestoreDiffService.MaxContentSnapshotRows, "Restore content snapshot should use the default row limit when unset.");
            Assert(BackupRestoreDiffService.ResolveMaxContentSnapshotRows(BackupRestoreDiffService.MaxConfigurableContentSnapshotRows + 10) == BackupRestoreDiffService.MaxConfigurableContentSnapshotRows, "Restore content snapshot should clamp oversized row limits.");
            BackupMirrorSettings.RestoreContentSnapshotMaxRows = 75;
            MethodInfo restoreRowsMethod = typeof(Form1).GetMethod("GetRestoreContentSnapshotMaxRows", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(restoreRowsMethod != null, "Form1 should expose the restore content snapshot row setting internally.");
            Assert((int)restoreRowsMethod.Invoke(null, null) == 75, "Restore snapshot capture should read the configured content sample row limit.");
        }
        finally
        {
            BackupMirrorSettings.RestoreContentSnapshotMaxRows = oldRestoreContentSnapshotMaxRows;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static DataTable BuildRestoreColumnMetadata(IEnumerable<string[]> rows)
    {
        DataTable table = new DataTable();
        table.Columns.Add("Name");
        table.Columns.Add("DataType");
        table.Columns.Add("IsNullable");
        table.Columns.Add("Default");
        table.Columns.Add("Comment");
        table.Columns.Add("OrdinalPosition");
        foreach (string[] row in rows)
        {
            table.Rows.Add(row);
        }
        return table;
    }

    private static DataTable BuildPagedRestoreContentRows(long offset, int limit, long changedId)
    {
        DataTable table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("payload", typeof(byte[]));
        for (int i = 0; i < limit; i++)
        {
            long id = offset + i + 1;
            string status = id == changedId ? "changed" : "status-" + (id % 7);
            table.Rows.Add((int)id, status, new byte[] { (byte)(id % 255) });
        }
        return table;
    }

    private static DataTable BuildRestoreContentRows(IEnumerable<object[]> rows)
    {
        DataTable table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("payload", typeof(byte[]));
        foreach (object[] row in rows)
        {
            object payload = row[2] == null ? DBNull.Value : row[2];
            table.Rows.Add(row[0], row[1], payload);
        }
        return table;
    }

    private static void TestTableDesignerCommentDictionaryDiff()
    {
        Dictionary<string, string> existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "NAME", "名稱" },
            { "STATUS", "狀態" },
            { "OLD_ONLY", "舊欄位" }
        };
        Dictionary<string, string> imported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "name", "姓名" },
            { "STATUS", "狀態" },
            { "NEW_ONLY", "新欄位" }
        };

        TableDesignerForm.AutoColumnCommentDictionaryDiffReport report =
            TableDesignerForm.BuildAutoColumnCommentDictionaryDiffReport(existing, imported);

        Assert(report.ImportedCount == 3, "Diff report should count imported entries.");
        Assert(report.Added == 1, "Diff report should count added entries.");
        Assert(report.Updated == 1, "Diff report should count updated entries.");
        Assert(report.Removed == 1, "Diff report should count removed entries.");
        Assert(report.Unchanged == 1, "Diff report should count unchanged entries.");
        Assert(report.Entries.Count == 4, "Diff report should include every changed and unchanged entry.");

        TableDesignerForm.AutoColumnCommentDictionaryDiffEntry updated = report.Entries.Find(e => e.Status == "updated");
        Assert(updated != null && updated.Key.Equals("name", StringComparison.OrdinalIgnoreCase), "Diff report should include updated key.");
        Assert(updated.ExistingValue == "名稱" && updated.ImportedValue == "姓名", "Updated entry should keep both old and new comments.");

        TableDesignerForm.AutoColumnCommentDictionaryDiffEntry removed = report.Entries.Find(e => e.Status == "removed");
        Assert(removed != null && removed.Key == "OLD_ONLY" && removed.ExistingValue == "舊欄位", "Removed entry should keep the current comment.");

        string signedPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_comment_dictionary_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            TableDesignerForm.ExportAutoColumnCommentDictionaryFile(signedPath, imported);
            TableDesignerForm.AutoColumnCommentDictionaryImportPreview signedPreview =
                TableDesignerForm.PreviewAutoColumnCommentDictionaryImportFile(signedPath);
            Assert(signedPreview.SignatureInfo.SignaturePresent, "Exported comment dictionary should include a signature.");
            Assert(signedPreview.SignatureInfo.SignatureValid, "Exported comment dictionary signature should validate.");
            Assert(signedPreview.Comments.ContainsKey("NEW_ONLY"), "Signed comment dictionary preview should keep comments.");

            JObject tampered = JObject.Parse(File.ReadAllText(signedPath, Encoding.UTF8));
            tampered["columns"]["NEW_ONLY"] = "被修改";
            File.WriteAllText(signedPath, tampered.ToString(Formatting.Indented), Encoding.UTF8);
            TableDesignerForm.AutoColumnCommentDictionaryImportPreview tamperedPreview =
                TableDesignerForm.PreviewAutoColumnCommentDictionaryImportFile(signedPath);
            Assert(tamperedPreview.SignatureInfo.SignaturePresent, "Tampered comment dictionary should still report the stored signature.");
            Assert(!tamperedPreview.SignatureInfo.SignatureValid, "Tampered comment dictionary signature should fail validation.");

            File.WriteAllText(signedPath, "{ \"LEGACY\": \"舊格式\" }", Encoding.UTF8);
            TableDesignerForm.AutoColumnCommentDictionaryImportPreview legacyPreview =
                TableDesignerForm.PreviewAutoColumnCommentDictionaryImportFile(signedPath);
            Assert(!legacyPreview.SignatureInfo.SignaturePresent, "Legacy comment dictionary should be accepted without a signature.");
            Assert(legacyPreview.Comments["LEGACY"] == "舊格式", "Legacy comment dictionary should still parse key/value JSON.");
        }
        finally
        {
            if (File.Exists(signedPath)) File.Delete(signedPath);
        }

        string dictionaryErrorLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            try
            {
                TableDesignerForm.ImportAutoColumnCommentDictionaryFile("");
                Assert(false, "Auto comment dictionary import should require a source path.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "請指定檔案路徑", "Auto comment dictionary import source validation should localize Traditional Chinese messages.");
            }

            try
            {
                TableDesignerForm.ExportAutoColumnCommentDictionaryFile("", imported);
                Assert(false, "Auto comment dictionary export should require a target path.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "請指定輸出目標路徑", "Auto comment dictionary export target validation should localize Traditional Chinese messages.");
            }

            string emptyDictionaryPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_comment_dictionary_empty_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(emptyDictionaryPath, "", Encoding.UTF8);
                TableDesignerForm.PreviewAutoColumnCommentDictionaryImportFile(emptyDictionaryPath);
                Assert(false, "Auto comment dictionary preview should reject empty payloads.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "字典回傳格式不正確", "Auto comment dictionary empty payload errors should localize Traditional Chinese messages.");
            }
            finally
            {
                if (File.Exists(emptyDictionaryPath)) File.Delete(emptyDictionaryPath);
            }

            MethodInfo loadCoreMethod = typeof(TableDesignerForm).GetMethod("LoadAutoColumnCommentsCore", BindingFlags.Static | BindingFlags.NonPublic);
            Dictionary<string, string> cachedDictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "CACHE_ONLY", "快取欄位" }
            };
            Dictionary<string, string> loadedFromCache = (Dictionary<string, string>)loadCoreMethod.Invoke(null, new object[]
            {
                new Func<string>(() => { throw new InvalidOperationException("remote down"); }),
                new Action<Dictionary<string, string>>(_ => { }),
                new Func<Dictionary<string, string>>(() => cachedDictionary)
            });
            Assert(loadedFromCache.ContainsKey("CACHE_ONLY"), "Auto comment dictionary should fall back to local cache when remote load fails.");
            AssertContains(TableDesignerForm.GetAutoColumnCommentLastError(), "已改用本機快取", "Auto comment dictionary cache fallback should localize Traditional Chinese status.");

            Dictionary<string, string> loadedFromCacheAfterEmptyError = (Dictionary<string, string>)loadCoreMethod.Invoke(null, new object[]
            {
                new Func<string>(() => { throw new Exception(""); }),
                new Action<Dictionary<string, string>>(_ => { }),
                new Func<Dictionary<string, string>>(() => cachedDictionary)
            });
            Assert(loadedFromCacheAfterEmptyError.ContainsKey("CACHE_ONLY"), "Auto comment dictionary should fall back to local cache when remote error text is empty.");
            AssertContains(TableDesignerForm.GetAutoColumnCommentLastError(), "未知錯誤", "Auto comment dictionary empty remote errors should localize Traditional Chinese fallback text.");
            MethodInfo autoCommentExceptionMessageMethod = typeof(TableDesignerForm).GetMethod("BuildAutoColumnCommentExceptionMessage", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(autoCommentExceptionMessageMethod != null, "Auto comment dictionary exception message helper should be testable.");
            AssertEquals("匯入自動註解字典失敗：未知錯誤", (string)autoCommentExceptionMessageMethod.Invoke(null, new object[] { "Designer.AutoCommentsImportFailed", new Exception("") }), "Blank auto comment import errors should localize Traditional Chinese unknown errors.");
            AssertEquals("匯出自動註解字典失敗：disk full", (string)autoCommentExceptionMessageMethod.Invoke(null, new object[] { "Designer.AutoCommentsExportFailed", new IOException(" disk full ") }), "Auto comment export errors should preserve explicit Traditional Chinese reasons.");
            AssertEquals("保存註解字典失敗：未知錯誤", (string)autoCommentExceptionMessageMethod.Invoke(null, new object[] { "Designer.AutoCommentsDictionarySaveFailed", new Exception("   ") }), "Blank auto comment dictionary save errors should localize Traditional Chinese unknown errors.");
            AssertEquals("切換註解字典失敗：未知錯誤", (string)autoCommentExceptionMessageMethod.Invoke(null, new object[] { "Designer.AutoCommentsDictionarySwitchFailed", new Exception("") }), "Blank auto comment dictionary switch errors should localize Traditional Chinese unknown errors.");
            AssertEquals("重新命名註解字典失敗：未知錯誤", (string)autoCommentExceptionMessageMethod.Invoke(null, new object[] { "Designer.AutoCommentsDictionaryRenameFailed", new Exception("") }), "Blank auto comment dictionary rename errors should localize Traditional Chinese unknown errors.");
            AssertEquals("刪除註解字典失敗：未知錯誤", (string)autoCommentExceptionMessageMethod.Invoke(null, new object[] { "Designer.AutoCommentsDictionaryDeleteFailed", new Exception("") }), "Blank auto comment dictionary delete errors should localize Traditional Chinese unknown errors.");
            AssertEquals("比較註解字典版本失敗：未知錯誤", (string)autoCommentExceptionMessageMethod.Invoke(null, new object[] { "Designer.AutoCommentsDictionaryVersionCompareFailed", new Exception("") }), "Blank auto comment dictionary version compare errors should localize Traditional Chinese unknown errors.");
            AssertEquals("回復註解字典版本失敗：未知錯誤", (string)autoCommentExceptionMessageMethod.Invoke(null, new object[] { "Designer.AutoCommentsDictionaryVersionRestoreFailed", new Exception("") }), "Blank auto comment dictionary version restore errors should localize Traditional Chinese unknown errors.");

            Localization.SetLanguage(Localization.English, false);
            MethodInfo autoCommentErrorMethod = typeof(TableDesignerForm).GetMethod("GetAutoColumnCommentErrorMessage", BindingFlags.Static | BindingFlags.NonPublic);
            AssertEquals("Unknown error", (string)autoCommentErrorMethod.Invoke(null, new object[] { new Exception("") }), "Auto comment dictionary empty errors should localize English fallback text.");
            AssertEquals("Import auto comment dictionary failed: Unknown error", (string)autoCommentExceptionMessageMethod.Invoke(null, new object[] { "Designer.AutoCommentsImportFailed", new Exception("") }), "Blank auto comment import errors should localize English unknown errors.");
            AssertEquals("Restore comment dictionary version failed: version missing", (string)autoCommentExceptionMessageMethod.Invoke(null, new object[] { "Designer.AutoCommentsDictionaryVersionRestoreFailed", new InvalidOperationException(" version missing ") }), "Auto comment dictionary version restore errors should preserve explicit English reasons.");
            try
            {
                TableDesignerForm.PreviewAutoColumnCommentDictionaryImportFile("");
                Assert(false, "Auto comment dictionary preview should require a source path.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "File path is required", "Auto comment dictionary preview source validation should localize English messages.");
            }

            string invalidDictionaryPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_comment_dictionary_invalid_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(invalidDictionaryPath, "[]", Encoding.UTF8);
                TableDesignerForm.PreviewAutoColumnCommentDictionaryImportFile(invalidDictionaryPath);
                Assert(false, "Auto comment dictionary preview should reject non-object JSON.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "auto comment dictionary format is invalid", "Auto comment dictionary invalid format errors should localize English messages.");
            }
            finally
            {
                if (File.Exists(invalidDictionaryPath)) File.Delete(invalidDictionaryPath);
            }
        }
        finally
        {
            Localization.SetLanguage(dictionaryErrorLanguage, false);
        }
    }

    private static void TestTableDesignerCommentDictionaryVersions()
    {
        string dictionaryName = "codex_smoke_" + Guid.NewGuid().ToString("N");
        try
        {
            Dictionary<string, string> first = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "NAME", "名稱" },
                { "STATUS", "狀態" }
            };
            Dictionary<string, string> second = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "NAME", "姓名" },
                { "NEW_ONLY", "新欄位" }
            };

            TableDesignerForm.SaveNamedAutoColumnCommentDictionaryFile(dictionaryName, first);
            TableDesignerForm.SaveNamedAutoColumnCommentDictionaryFile(dictionaryName, second);

            List<TableDesignerForm.AutoColumnCommentDictionaryVersionInfo> versions =
                TableDesignerForm.ListNamedAutoColumnCommentDictionaryVersions(dictionaryName);
            Assert(versions.Count >= 1, "Saving over a named dictionary should keep the previous version.");
            Assert(versions[0].EntryCount == 2, "Dictionary version metadata should count entries.");

            TableDesignerForm.AutoColumnCommentDictionaryDiffReport report =
                TableDesignerForm.BuildNamedAutoColumnCommentDictionaryVersionDiffReport(dictionaryName, versions[0].VersionId);
            Assert(report.Added == 1, "Version diff should report fields restored from the older version.");
            Assert(report.Updated == 1, "Version diff should report changed comments.");
            Assert(report.Removed == 1, "Version diff should report fields removed by restoring an older version.");

            Dictionary<string, string> restored =
                TableDesignerForm.RestoreNamedAutoColumnCommentDictionaryVersion(dictionaryName, versions[0].VersionId);
            Assert(restored.ContainsKey("STATUS"), "Restore should bring back fields from the selected version.");
            Assert(restored["NAME"] == "名稱", "Restore should apply the selected version content.");

            Dictionary<string, string> loaded = TableDesignerForm.LoadNamedAutoColumnCommentDictionaryFile(dictionaryName);
            Assert(loaded.ContainsKey("STATUS") && loaded["NAME"] == "名稱", "Restored named dictionary should be readable as the current dictionary.");
        }
        finally
        {
            CleanupNamedAutoColumnCommentDictionary(dictionaryName);
        }
    }

    private static void TestDatabaseDumpService()
    {
        FakeDumpDatabase db = new FakeDumpDatabase();
        string dump = DatabaseDumpService.BuildDatabaseDump(db, "main");

        AssertContains(dump, "-- mySQLPunk Database Backup", "Database dump service should emit the backup header.");
        AssertContains(dump, "CREATE TABLE \"public\".\"users\"", "Database dump service should include table DDL.");
        AssertContains(dump, "INSERT INTO \"public.users\" (\"id\", \"name\", \"payload\") VALUES (1, 'O''Reilly', '\\x0102');", "Database dump service should preserve existing INSERT target behavior and escaped values.");
        AssertContains(dump, "CREATE VIEW \"public\".\"active_users\"", "Database dump service should include view DDL.");

        AssertEquals("\"public\".\"users\"", DatabaseDumpService.BuildQualifiedObjectName(db, "main", "public.users"), "PostgreSQL qualified table name should preserve schema.");
        AssertEquals("'\\x0A0B'", DatabaseDumpService.ToSqlLiteral(db, new byte[] { 0x0A, 0x0B }), "PostgreSQL byte literal should be hex escaped.");

        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            try
            {
                DatabaseDumpService.WriteDatabaseDump(db, "main", "");
                Assert(false, "Database dump should require a target path.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "請指定輸出目標路徑", "Database dump target path validation should localize Traditional Chinese errors.");
            }

            Localization.SetLanguage(Localization.English, false);
            try
            {
                DatabaseDumpService.WriteDatabaseDump(db, "main", "");
                Assert(false, "Database dump should require a target path in English.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "Target path is required", "Database dump target path validation should localize English errors.");
            }

            Type treeDatabaseTargetType = typeof(Form1).GetNestedType("TreeDatabaseTarget", BindingFlags.NonPublic);
            MethodInfo createDatabaseBackupMethod = typeof(Form1).GetMethod("CreateDatabaseBackup", BindingFlags.Instance | BindingFlags.NonPublic);
            object form = FormatterServices.GetUninitializedObject(typeof(Form1));
            object target = FormatterServices.GetUninitializedObject(treeDatabaseTargetType);

            Localization.SetLanguage(Localization.TraditionalChinese, false);
            try
            {
                createDatabaseBackupMethod.Invoke(form, new object[] { target, "" });
                Assert(false, "Form database backup should require a target path.");
            }
            catch (TargetInvocationException ex)
            {
                ArgumentException argumentException = ex.InnerException as ArgumentException;
                Assert(argumentException != null, "Form database backup should throw ArgumentException for missing target paths.");
                AssertContains(argumentException.Message, "請指定輸出目標路徑", "Form database backup target path validation should localize Traditional Chinese errors.");
            }

            Localization.SetLanguage(Localization.English, false);
            try
            {
                createDatabaseBackupMethod.Invoke(form, new object[] { target, "" });
                Assert(false, "Form database backup should require a target path in English.");
            }
            catch (TargetInvocationException ex)
            {
                ArgumentException argumentException = ex.InnerException as ArgumentException;
                Assert(argumentException != null, "Form database backup should throw ArgumentException for missing target paths in English.");
                AssertContains(argumentException.Message, "Target path is required", "Form database backup target path validation should localize English errors.");
            }
        }
        finally
        {
            Localization.SetLanguage(previousLanguage, false);
        }
    }

    private static void TestSqliteColumnCommentExchangeService()
    {
        FakeSqliteCommentDatabase db = new FakeSqliteCommentDatabase();
        SqliteColumnCommentExportResult result;
        string json = SqliteColumnCommentExchangeService.BuildExportJson(db, "main", null, out result);

        Assert(result.TableCount == 1, "SQLite comment export should skip tables without comments.");
        Assert(result.CommentCount == 2, "SQLite comment export should count non-empty comments.");
        AssertContains(json, "\"provider\": \"sqlite\"", "SQLite comment export should mark provider.");
        AssertContains(json, "\"users\"", "SQLite comment export should include table name.");
        AssertContains(json, "\"NAME\": \"姓名\"", "SQLite comment export should include comments.");

        SqliteColumnCommentImportPlan plan = SqliteColumnCommentExchangeService.BuildImportPlan(json);
        Assert(plan.TableCount == 1 && plan.CommentCount == 2, "SQLite comment import plan should count imported comments.");
        AssertContains(string.Join("\n", plan.Statements.ToArray()), "CREATE TABLE IF NOT EXISTS \"__mysqlpunk_column_comments\"", "SQLite comment import should ensure sidecar table.");
        AssertContains(string.Join("\n", plan.Statements.ToArray()), "DELETE FROM \"__mysqlpunk_column_comments\" WHERE table_name = 'users';", "SQLite comment import should replace selected table comments.");

        string legacyJson = "{ \"users\": { \"TITLE\": \"標題\", \"QUOTE\": \"O'Reilly\" }, \"logs\": { \"MESSAGE\": \"訊息\" } }";
        SqliteColumnCommentImportPlan filteredPlan = SqliteColumnCommentExchangeService.BuildImportPlan(legacyJson, "users");
        string filteredSql = string.Join("\n", filteredPlan.Statements.ToArray());
        Assert(filteredPlan.TableCount == 1 && filteredPlan.CommentCount == 2, "SQLite comment import should support legacy table-map JSON and table filters.");
        AssertContains(filteredSql, "'O''Reilly'", "SQLite comment import should escape single quotes.");
        AssertNotContains(filteredSql, "logs", "SQLite comment table filter should skip other tables.");
        SqliteColumnCommentImportReviewReport filteredReview =
            SqliteColumnCommentExchangeService.BuildImportReviewReport(db, "main", filteredPlan);
        Assert(filteredReview.Added == 2 && filteredReview.Removed == 2, "SQLite comment import review should show added and removed comments for replace semantics.");
        Assert(filteredReview.Entries.Count == 4, "SQLite comment import review should include one entry per changed current/imported comment.");
        string reviewLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            AssertContains(SqliteColumnCommentExchangeService.BuildImportReviewSummary(filteredReview), "新增 2", "SQLite comment import review summary should include added count.");
            Localization.SetLanguage(Localization.English, false);
            AssertContains(SqliteColumnCommentExchangeService.BuildImportReviewSummary(filteredReview), "Review summary: added 2", "SQLite comment import review summary should support English.");
        }
        finally
        {
            Localization.SetLanguage(reviewLanguage, false);
        }
        string reviewDirectory = Path.Combine(Path.GetTempPath(), "sqlite_comment_review_" + Guid.NewGuid().ToString("N"));
        try
        {
            filteredReview.SourcePath = "legacy.json";
            string reviewPath = SqliteColumnCommentExchangeService.WriteImportReviewReport(filteredReview, reviewDirectory);
            Assert(File.Exists(reviewPath), "SQLite comment import review should write a JSON report.");
            JObject reviewJson = JObject.Parse(File.ReadAllText(reviewPath, Encoding.UTF8));
            Assert((int)reviewJson["Added"] == 2 && (int)reviewJson["Removed"] == 2, "SQLite comment import review JSON should include diff counts.");
            Assert(reviewJson["Entries"] != null && reviewJson["Entries"].HasValues, "SQLite comment import review JSON should include entry details.");
        }
        finally
        {
            if (Directory.Exists(reviewDirectory)) Directory.Delete(reviewDirectory, true);
        }
        string reviewReportLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            try
            {
                SqliteColumnCommentExchangeService.WriteImportReviewReport(filteredReview, "");
                Assert(false, "SQLite comment import review should require a report directory.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "請指定 SQLite 欄位註解審核報表資料夾", "SQLite comment review report directory validation should localize Traditional Chinese messages.");
            }
        }
        finally
        {
            Localization.SetLanguage(reviewReportLanguage, false);
        }

        string sqliteCommentErrorLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            SqliteColumnCommentCliResult missingValueResult = SqliteColumnCommentCliService.TryRun(new[]
            {
                "--sqlite-comments-export",
                "--database"
            });
            Assert(missingValueResult.Handled && missingValueResult.ExitCode == 1, "SQLite comment CLI should reject options without values.");
            AssertContains(missingValueResult.Message, "請提供 --database 的參數值", "SQLite comment CLI missing option values should localize Traditional Chinese messages.");
            AssertEquals("未知錯誤", SqliteColumnCommentCliService.BuildCliFailureMessage(new Exception("")), "SQLite comment CLI blank errors should localize Traditional Chinese unknown errors.");

            try
            {
                SqliteColumnCommentExchangeService.BuildImportPlan("");
                Assert(false, "SQLite comment JSON import should reject empty content.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "SQLite 欄位註解交換檔案是空的", "SQLite comment empty JSON errors should localize Traditional Chinese messages.");
            }

            try
            {
                SqliteColumnCommentExchangeService.BuildImportPlanFromCsv("table,column\r\nusers,NAME\r\n");
                Assert(false, "SQLite comment CSV import should require comment headers.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "必須包含 table、column 與 comment 欄位標題", "SQLite comment CSV header errors should localize Traditional Chinese messages.");
            }

            try
            {
                SqliteColumnCommentExchangeService.BuildImportPlanFromFile("");
                Assert(false, "SQLite comment import from file should require a source path.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "請指定檔案路徑", "SQLite comment source path validation should localize Traditional Chinese messages.");
            }

            Localization.SetLanguage(Localization.English, false);
            SqliteColumnCommentCliResult missingRequiredResult = SqliteColumnCommentCliService.TryRun(new[]
            {
                "--sqlite-comments-export"
            });
            Assert(missingRequiredResult.Handled && missingRequiredResult.ExitCode == 1, "SQLite comment CLI should reject missing required options.");
            AssertContains(missingRequiredResult.Message, "Missing required option --database", "SQLite comment CLI missing required options should localize English messages.");
            AssertEquals("Unknown error", SqliteColumnCommentCliService.BuildCliFailureMessage(new Exception("   ")), "SQLite comment CLI blank errors should localize English unknown errors.");
            AssertEquals("database locked", SqliteColumnCommentCliService.BuildCliFailureMessage(new InvalidOperationException(" database locked ")), "SQLite comment CLI should preserve explicit English reasons.");

            try
            {
                SqliteColumnCommentExchangeService.BuildImportPlan("[]");
                Assert(false, "SQLite comment JSON import should reject empty arrays.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "has no usable comments", "SQLite comment empty JSON arrays should localize English messages.");
            }

            try
            {
                SqliteColumnCommentExchangeService.BuildImportPlan("123");
                Assert(false, "SQLite comment JSON import should reject unsupported roots.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "is not a supported JSON object", "SQLite comment unsupported JSON errors should localize English messages.");
            }

            try
            {
                SqliteColumnCommentExchangeService.BuildImportPlanFromCsv("");
                Assert(false, "SQLite comment CSV import should reject empty content.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "SQLite column comment CSV is empty", "SQLite comment empty CSV errors should localize English messages.");
            }

            try
            {
                SqliteColumnCommentExchangeService.BuildImportPlanFromYaml("");
                Assert(false, "SQLite comment YAML import should reject empty content.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "SQLite column comment YAML is empty", "SQLite comment empty YAML errors should localize English messages.");
            }

            try
            {
                SqliteColumnCommentExchangeService.BuildImportPlanFromYaml("comments:\n- table: logs\n  column: MESSAGE\n  comment: text\n", "users");
                Assert(false, "SQLite comment YAML import should reject filters with no usable comments.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "YAML has no usable comments", "SQLite comment YAML no-usable-comment errors should localize English messages.");
            }

            string invalidXlsxPath = Path.Combine(Path.GetTempPath(), "sqlite_comments_invalid_" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                using (ZipArchive archive = ZipFile.Open(invalidXlsxPath, ZipArchiveMode.Create))
                {
                    ZipArchiveEntry entry = archive.CreateEntry("[Content_Types].xml");
                    using (StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                    {
                        writer.Write("<Types />");
                    }
                }

                SqliteColumnCommentExchangeService.ParseExchangeXlsx(invalidXlsxPath);
                Assert(false, "SQLite comment XLSX import should reject workbooks without a first worksheet.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "XLSX has no first worksheet", "SQLite comment XLSX worksheet errors should localize English messages.");
            }
            finally
            {
                if (File.Exists(invalidXlsxPath)) File.Delete(invalidXlsxPath);
            }

            try
            {
                SqliteColumnCommentExchangeService.WriteExportFile(db, "main", "users", "");
                Assert(false, "SQLite comment export should require a target path.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "Target path is required", "SQLite comment target path validation should localize English messages.");
            }

            try
            {
                SqliteColumnCommentExchangeService.BuildExportJson(new FakeDumpDatabase(), "main", "users", out result);
                Assert(false, "SQLite comment export should reject non-SQLite connections.");
            }
            catch (NotSupportedException ex)
            {
                AssertContains(ex.Message, "only supports SQLite connections", "SQLite comment provider validation should localize English messages.");
            }
        }
        finally
        {
            Localization.SetLanguage(sqliteCommentErrorLanguage, false);
        }

        string flatArrayJson = "[{\"table\":\"users\",\"column\":\"EMAIL\",\"comment\":\"電子郵件\"},{\"table\":\"logs\",\"column\":\"MESSAGE\",\"comment\":\"訊息\"}]";
        SqliteColumnCommentImportPlan flatArrayPlan = SqliteColumnCommentExchangeService.BuildImportPlan(flatArrayJson, "users");
        string flatArraySql = string.Join("\n", flatArrayPlan.Statements.ToArray());
        Assert(flatArrayPlan.TableCount == 1 && flatArrayPlan.CommentCount == 1, "SQLite comment import should support third-party flat array JSON.");
        AssertContains(flatArraySql, "'EMAIL'", "SQLite comment flat array import should include selected column.");
        AssertNotContains(flatArraySql, "MESSAGE", "SQLite comment flat array import should honor table filters.");

        string tableColumnArrayJson = "{ \"tables\": [{ \"name\": \"users\", \"columns\": [{ \"name\": \"PHONE\", \"description\": \"電話\" }] }] }";
        SqliteColumnCommentImportPlan tableColumnArrayPlan = SqliteColumnCommentExchangeService.BuildImportPlan(tableColumnArrayJson);
        Assert(tableColumnArrayPlan.TableCount == 1 && tableColumnArrayPlan.CommentCount == 1, "SQLite comment import should support table column-array JSON.");

        string aliasArrayJson = "[{\"object_name\":\"users\",\"field_name\":\"NICKNAME\",\"comment_text\":\"暱稱\"},{\"object_name\":\"logs\",\"field_name\":\"MESSAGE\",\"comment_text\":\"訊息\"}]";
        SqliteColumnCommentImportPlan aliasArrayPlan = SqliteColumnCommentExchangeService.BuildImportPlan(aliasArrayJson, "users");
        string aliasArraySql = string.Join("\n", aliasArrayPlan.Statements.ToArray());
        Assert(aliasArrayPlan.TableCount == 1 && aliasArrayPlan.CommentCount == 1, "SQLite comment import should support object/field/comment aliases.");
        AssertContains(aliasArraySql, "'NICKNAME'", "SQLite comment alias import should include selected aliased column.");
        AssertNotContains(aliasArraySql, "MESSAGE", "SQLite comment alias import should honor table filters.");

        string csv = "table,column,comment\r\nusers,ADDRESS,\"地址, 含逗號\"\r\nlogs,MESSAGE,訊息\r\n";
        SqliteColumnCommentImportPlan csvPlan = SqliteColumnCommentExchangeService.BuildImportPlanFromCsv(csv, "users");
        string csvSql = string.Join("\n", csvPlan.Statements.ToArray());
        Assert(csvPlan.TableCount == 1 && csvPlan.CommentCount == 1, "SQLite comment import should support CSV with table filters.");
        AssertContains(csvSql, "地址, 含逗號", "SQLite comment CSV import should preserve quoted commas.");
        AssertNotContains(csvSql, "MESSAGE", "SQLite comment CSV import should honor table filters.");

        string aliasCsv = "object_name,field_name,comment_text\r\nusers,ALIAS,別名\r\nlogs,MESSAGE,訊息\r\n";
        SqliteColumnCommentImportPlan aliasCsvPlan = SqliteColumnCommentExchangeService.BuildImportPlanFromCsv(aliasCsv, "users");
        string aliasCsvSql = string.Join("\n", aliasCsvPlan.Statements.ToArray());
        Assert(aliasCsvPlan.TableCount == 1 && aliasCsvPlan.CommentCount == 1, "SQLite comment CSV import should support third-party alias headers.");
        AssertContains(aliasCsvSql, "'ALIAS'", "SQLite comment CSV alias import should include selected column.");

        SqliteColumnCommentExportResult csvExportResult;
        string exportCsv = SqliteColumnCommentExchangeService.BuildExportCsv(db, "main", "users", out csvExportResult);
        Assert(csvExportResult.TableCount == 1 && csvExportResult.CommentCount == 2, "SQLite comment CSV export should count exported comments.");
        AssertContains(exportCsv, "table,column,comment", "SQLite comment CSV export should include headers.");
        AssertContains(exportCsv, "users,NAME", "SQLite comment CSV export should include table and column.");

        string yaml = "comments:\n- table: users\n  column: NOTE\n  comment: \"備註: 可含冒號\"\n- table: logs\n  column: MESSAGE\n  comment: 訊息\n";
        SqliteColumnCommentImportPlan yamlPlan = SqliteColumnCommentExchangeService.BuildImportPlanFromYaml(yaml, "users");
        string yamlSql = string.Join("\n", yamlPlan.Statements.ToArray());
        Assert(yamlPlan.TableCount == 1 && yamlPlan.CommentCount == 1, "SQLite comment import should support YAML with table filters.");
        AssertContains(yamlSql, "備註: 可含冒號", "SQLite comment YAML import should preserve quoted values.");
        AssertNotContains(yamlSql, "MESSAGE", "SQLite comment YAML import should honor table filters.");

        string aliasYaml = "comments:\n- entity: users\n  attribute: BIO\n  note: 個人簡介\n- entity: logs\n  attribute: MESSAGE\n  note: 訊息\n";
        SqliteColumnCommentImportPlan aliasYamlPlan = SqliteColumnCommentExchangeService.BuildImportPlanFromYaml(aliasYaml, "users");
        string aliasYamlSql = string.Join("\n", aliasYamlPlan.Statements.ToArray());
        Assert(aliasYamlPlan.TableCount == 1 && aliasYamlPlan.CommentCount == 1, "SQLite comment YAML import should support entity/attribute/note aliases.");
        AssertContains(aliasYamlSql, "'BIO'", "SQLite comment YAML alias import should include selected column.");
        AssertNotContains(aliasYamlSql, "MESSAGE", "SQLite comment YAML alias import should honor table filters.");

        string missingXlsxPath = Path.Combine(Path.GetTempPath(), "sqlite_comments_missing_" + Guid.NewGuid().ToString("N") + ".xlsx");
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            try
            {
                SqliteColumnCommentExchangeService.ParseExchangeXlsx(missingXlsxPath);
                Assert(false, "SQLite comment XLSX import should fail when the file is missing.");
            }
            catch (FileNotFoundException ex)
            {
                AssertContains(ex.Message, "找不到 SQLite 欄位註解 XLSX 檔案", "SQLite comment XLSX missing file errors should localize Traditional Chinese messages.");
            }

            Localization.SetLanguage(Localization.English, false);
            try
            {
                SqliteColumnCommentExchangeService.ParseExchangeXlsx(missingXlsxPath);
                Assert(false, "SQLite comment XLSX import should fail when the file is missing in English.");
            }
            catch (FileNotFoundException ex)
            {
                AssertContains(ex.Message, "SQLite column comment XLSX file does not exist", "SQLite comment XLSX missing file errors should localize English messages.");
            }
        }
        finally
        {
            Localization.SetLanguage(previousLanguage, false);
        }

        SqliteColumnCommentExportResult yamlExportResult;
        string exportYaml = SqliteColumnCommentExchangeService.BuildExportYaml(db, "main", "users", out yamlExportResult);
        Assert(yamlExportResult.TableCount == 1 && yamlExportResult.CommentCount == 2, "SQLite comment YAML export should count exported comments.");
        AssertContains(exportYaml, "comments:", "SQLite comment YAML export should include comments root.");
        AssertContains(exportYaml, "column: \"NAME\"", "SQLite comment YAML export should include column names.");

        string xlsxPath = Path.Combine(Path.GetTempPath(), "sqlite_comments_" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            SqliteColumnCommentExportResult xlsxExportResult =
            SqliteColumnCommentExchangeService.WriteExportFile(db, "main", "users", xlsxPath);
            Assert(xlsxExportResult.TableCount == 1 && xlsxExportResult.CommentCount == 2, "SQLite comment XLSX export should count exported comments.");
            Assert(File.Exists(xlsxPath), "SQLite comment XLSX export should write a workbook.");
            string xlsxRowsText = string.Join("\n", ReadXlsxRowsForTest(xlsxPath).Select(row => string.Join("|", row)).ToArray());
            AssertContains(xlsxRowsText, "provider|database|table|column|type|not_null|default_value|comment", "SQLite comment XLSX export should include richer template columns.");
            AssertContains(xlsxRowsText, "varchar", "SQLite comment XLSX export should include column type metadata.");
            AssertContains(xlsxRowsText, "CURRENT_TIMESTAMP", "SQLite comment XLSX export should include default value metadata.");

            SqliteColumnCommentImportPlan xlsxPlan =
                SqliteColumnCommentExchangeService.BuildImportPlanFromFile(xlsxPath, "users");
            string xlsxSql = string.Join("\n", xlsxPlan.Statements.ToArray());
            Assert(xlsxPlan.TableCount == 1 && xlsxPlan.CommentCount == 2, "SQLite comment XLSX import should read exported comments.");
            AssertContains(xlsxSql, "'NAME'", "SQLite comment XLSX import should include exported columns.");
            AssertContains(xlsxSql, "'姓名'", "SQLite comment XLSX import should preserve Unicode comments.");
        }
        finally
        {
            if (File.Exists(xlsxPath)) File.Delete(xlsxPath);
        }

        string tempPath = Path.Combine(Path.GetTempPath(), "sqlite_comments_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempPath, legacyJson, Encoding.UTF8);
            SqliteColumnCommentImportPlan imported = SqliteColumnCommentExchangeService.ImportFromFile(db, tempPath, "users");
            Assert(imported.CommentCount == 2, "SQLite comment import from file should return import counts.");
            Assert(db.ExecutedSql.Count == imported.Statements.Count, "SQLite comment import from file should execute every planned statement.");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        string sqlitePath = Path.Combine(Path.GetTempPath(), "sqlite_comments_cli_" + Guid.NewGuid().ToString("N") + ".sqlite");
        string exportPath = Path.Combine(Path.GetTempPath(), "sqlite_comments_cli_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (my_sqlite sqliteDb = new my_sqlite())
            {
                sqliteDb.SetConn("Data Source=" + sqlitePath + ";Version=3;New=True;");
                sqliteDb.Open();
                sqliteDb.ExecSQL("CREATE TABLE users (ID INTEGER PRIMARY KEY, NAME TEXT);");
                sqliteDb.ExecSQL(SqliteColumnCommentExchangeService.BuildEnsureSidecarTableSql());
                sqliteDb.ExecSQL("INSERT OR REPLACE INTO " + my_sqlite.ColumnCommentTableName + " (table_name, column_name, comment) VALUES ('users', 'NAME', '姓名');");
            }

            SqliteColumnCommentCliResult exportResult = SqliteColumnCommentCliService.TryRun(new[]
            {
                "--sqlite-comments-export",
                "--database", sqlitePath,
                "--output", exportPath,
                "--table", "users"
            });
            Assert(exportResult.Handled && exportResult.ExitCode == 0, "SQLite comment CLI export should be handled successfully.");
            Assert(File.Exists(exportPath), "SQLite comment CLI export should write the JSON file.");
            AssertContains(File.ReadAllText(exportPath, Encoding.UTF8), "\"NAME\": \"姓名\"", "SQLite comment CLI export should include sidecar comments.");

            using (my_sqlite sqliteDb = new my_sqlite())
            {
                sqliteDb.SetConn("Data Source=" + sqlitePath + ";Version=3;");
                sqliteDb.Open();
                sqliteDb.ExecSQL("DELETE FROM " + my_sqlite.ColumnCommentTableName + ";");
            }

            SqliteColumnCommentCliResult importResult = SqliteColumnCommentCliService.TryRun(new[]
            {
                "--sqlite-comments-import",
                "--database", sqlitePath,
                "--input", exportPath,
                "--table", "users"
            });
            Assert(importResult.Handled && importResult.ExitCode == 0, "SQLite comment CLI import should be handled successfully.");

            using (my_sqlite sqliteDb = new my_sqlite())
            {
                sqliteDb.SetConn("Data Source=" + sqlitePath + ";Version=3;");
                sqliteDb.Open();
                DataTable importedComments = sqliteDb.SelectSQL("SELECT comment FROM " + my_sqlite.ColumnCommentTableName + " WHERE table_name = 'users' AND column_name = 'NAME';");
                Assert(importedComments.Rows.Count == 1, "SQLite comment CLI import should restore the sidecar row.");
                AssertEquals("姓名", importedComments.Rows[0]["comment"].ToString(), "SQLite comment CLI import should restore the comment text.");
            }
        }
        finally
        {
            if (File.Exists(exportPath)) File.Delete(exportPath);
            if (File.Exists(sqlitePath)) File.Delete(sqlitePath);
        }
    }

    private static List<List<string>> ReadXlsxRowsForTest(string xlsxPath)
    {
        MethodInfo method = typeof(SqliteColumnCommentExchangeService).GetMethod("ReadXlsxRows", BindingFlags.Static | BindingFlags.NonPublic);
        return (List<List<string>>)method.Invoke(null, new object[] { xlsxPath });
    }

    private static void TestSpatiaLiteRuntimeDiagnostics()
    {
        string root = Path.Combine(Path.GetTempPath(), "mysqlpunk_spatialite_" + Guid.NewGuid().ToString("N"));
        string toolsDir = Path.Combine(root, "tools", "spatialite");
        string cacheDir = Path.Combine(toolsDir, "cache");
        string offlineDir = Path.Combine(toolsDir, "offline");
        string runtimeDir = Path.Combine(root, "runtime");
        Directory.CreateDirectory(toolsDir);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(offlineDir);
        Directory.CreateDirectory(runtimeDir);
        File.WriteAllText(Path.Combine(toolsDir, "Build-SpatiaLiteRuntime.ps1"), "# test", Encoding.UTF8);
        string sourceArchivePath = Path.Combine(cacheDir, "libspatialite-5.1.0.zip");
        string offlineArchivePath = Path.Combine(offlineDir, "libspatialite-5.1.0.zip");
        File.WriteAllText(sourceArchivePath, "cached source", Encoding.UTF8);
        File.WriteAllText(offlineArchivePath, "offline source", Encoding.UTF8);
        string runtimeDllPath = Path.Combine(runtimeDir, "libspatialite.dll");
        File.WriteAllText(runtimeDllPath, "runtime dll", Encoding.UTF8);
        string runtimeDllHash = ComputeFileSha256ForTest(runtimeDllPath);
        File.WriteAllText(
            Path.Combine(runtimeDir, "SPATIALITE_RUNTIME_MANIFEST.json"),
            "{\"source_url\":\"https://www.gaia-gis.it/gaia-sins/libspatialite-sources/libspatialite-5.1.0.zip\",\"source_sha256\":\"abc123\",\"built_at_utc\":\"2026-05-24T00:00:00Z\",\"files\":[{\"name\":\"libspatialite.dll\",\"sha256\":\"" + runtimeDllHash + "\",\"bytes\":" + new FileInfo(runtimeDllPath).Length + "},{\"name\":\"missing_dependency.dll\",\"sha256\":\"deadbeef\",\"bytes\":12},{\"name\":\"../outside.dll\",\"sha256\":\"deadbeef\",\"bytes\":12}]}",
            Encoding.UTF8);
        string oldLanguage = Localization.CurrentLanguage;

        try
        {
            Localization.SetLanguage(Localization.English, false);
            string foundRoot = SpatiaLiteRuntimeDiagnosticService.FindRepositoryRoot(Path.Combine(root, "tools"));
            AssertEquals(root, foundRoot, "SpatiaLite diagnostics should locate the repository root from a child directory.");

            List<SpatiaLiteDiagnosticRow> rows = SpatiaLiteRuntimeDiagnosticService.BuildRows(runtimeDir, "missing mod_spatialite", root);
            string details = string.Join("\n", rows.Select(r => r.Item + "|" + r.Status + "|" + r.Detail).ToArray());
            AssertContains(details, "SpatiaLite Repair Script|Ready", "SpatiaLite diagnostics should detect the rebuild script.");
            AssertContains(details, "Build-SpatiaLiteRuntime.ps1", "SpatiaLite diagnostics should show the rebuild script path.");
            AssertContains(details, "powershell -ExecutionPolicy Bypass -File", "SpatiaLite diagnostics should show a runnable repair command.");
            AssertContains(details, "SpatiaLite Manifest Source|Info|Source: https://www.gaia-gis.it/gaia-sins/libspatialite-sources/libspatialite-5.1.0.zip", "SpatiaLite diagnostics should summarize the runtime manifest source.");
            AssertContains(details, "SHA-256: abc123", "SpatiaLite diagnostics should show the manifest source checksum.");
            AssertContains(details, "SpatiaLite Runtime File Verification|Warning|Verified 1, missing 1", "SpatiaLite diagnostics should verify manifest files and report missing runtime dependencies.");
            AssertContains(details, "missing_dependency.dll is missing", "SpatiaLite manifest verification should name missing runtime files.");
            AssertContains(details, "../outside.dll has an unsafe file name", "SpatiaLite manifest verification should reject paths outside the runtime directory.");
            AssertContains(details, "SpatiaLite Source Cache|Ready|" + sourceArchivePath, "SpatiaLite diagnostics should detect the cached source archive.");
            AssertContains(details, "SpatiaLite Offline Package|Ready|" + offlineArchivePath, "SpatiaLite diagnostics should detect the offline source package.");
            AssertContains(details, "SpatiaLite Cached Repair Command|Info|powershell -ExecutionPolicy Bypass -File", "SpatiaLite diagnostics should expose a cached repair command.");
            AssertContains(details, "-PreferCachedSource", "SpatiaLite cached repair command should prefer the cached source archive.");
            AssertContains(details, "-OfflinePackagePath", "SpatiaLite cached repair command should use a detected offline package.");
            AssertContains(details, "The repair command rebuilds the runtime from the official Gaia-SINS libspatialite 5.1.0 source", "SpatiaLite diagnostics should localize the English repair guide.");
            AssertContains(details, "SpatiaLite Repair Log|Info", "SpatiaLite diagnostics should show the repair log path.");
            AssertContains(details, "SpatiaLite Missing DLL|Warning", "SpatiaLite diagnostics should warn when mod_spatialite.dll is missing.");
            AssertContains(details, "missing mod_spatialite", "SpatiaLite diagnostics should keep the load error.");

            Localization.SetLanguage(Localization.TraditionalChinese, false);
            List<SpatiaLiteDiagnosticRow> zhRows = SpatiaLiteRuntimeDiagnosticService.BuildRows(runtimeDir, "missing mod_spatialite", root);
            string zhDetails = string.Join("\n", zhRows.Select(r => r.Item + "|" + r.Status + "|" + r.Detail).ToArray());
            AssertContains(zhDetails, "SpatiaLite Repair Script|就緒", "SpatiaLite diagnostics should localize ready status in Traditional Chinese.");
            AssertContains(zhDetails, "SpatiaLite Repair Log|資訊", "SpatiaLite diagnostics should localize info status in Traditional Chinese.");
            AssertContains(zhDetails, "SpatiaLite Missing DLL|警告", "SpatiaLite diagnostics should localize warning status in Traditional Chinese.");
            AssertContains(zhDetails, "SpatiaLite Manifest Source|資訊|來源：https://www.gaia-gis.it/gaia-sins/libspatialite-sources/libspatialite-5.1.0.zip", "SpatiaLite diagnostics should localize Traditional Chinese manifest source details.");
            AssertContains(zhDetails, "SpatiaLite Runtime File Verification|警告|已校驗 1，缺少 1", "SpatiaLite diagnostics should localize Traditional Chinese manifest verification summary.");
            AssertContains(zhDetails, "../outside.dll 檔名不安全", "SpatiaLite diagnostics should localize Traditional Chinese unsafe manifest file details.");
            AssertContains(zhDetails, "執行修復命令會從 Gaia-SINS 官方 libspatialite 5.1.0 原始碼重建 runtime", "SpatiaLite diagnostics should localize Traditional Chinese repair guide.");
            Localization.SetLanguage(Localization.English, false);

            System.Diagnostics.ProcessStartInfo startInfo = SpatiaLiteRuntimeDiagnosticService.BuildRepairProcessStartInfo(root);
            AssertEquals("powershell.exe", startInfo.FileName, "SpatiaLite repair should launch PowerShell.");
            AssertContains(startInfo.Arguments, "-NoExit", "SpatiaLite repair should keep the interactive window open by default.");
            AssertContains(startInfo.Arguments, "Tee-Object", "SpatiaLite repair should tee progress output to a log.");
            AssertContains(startInfo.Arguments, "Build-SpatiaLiteRuntime.ps1", "SpatiaLite repair should invoke the rebuild script.");
            Assert(startInfo.UseShellExecute, "SpatiaLite repair should open an interactive PowerShell window.");

            System.Diagnostics.ProcessStartInfo watchedStartInfo = SpatiaLiteRuntimeDiagnosticService.BuildRepairProcessStartInfo(root, false);
            Assert(!watchedStartInfo.Arguments.Contains("-NoExit"), "Watched SpatiaLite repair should exit so the UI can refresh diagnostics.");
            AssertContains(watchedStartInfo.Arguments, "Tee-Object", "Watched SpatiaLite repair should still tee progress output to a log.");
            Assert(watchedStartInfo.UseShellExecute, "Watched SpatiaLite repair should still open a PowerShell window.");

            string missingScriptRoot = Path.Combine(Path.GetTempPath(), "mysqlpunk_spatialite_missing_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(missingScriptRoot);
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            try
            {
                SpatiaLiteRuntimeDiagnosticService.BuildRepairProcessStartInfo(missingScriptRoot, false);
                Assert(false, "SpatiaLite repair process should require the rebuild script.");
            }
            catch (FileNotFoundException ex)
            {
                AssertContains(ex.Message, "找不到 SpatiaLite runtime 修復腳本", "SpatiaLite repair missing script errors should localize Traditional Chinese messages.");
            }

            Localization.SetLanguage(Localization.English, false);
            try
            {
                SpatiaLiteRuntimeDiagnosticService.BuildRepairProcessStartInfo(missingScriptRoot, false);
                Assert(false, "SpatiaLite repair process should require the rebuild script in English.");
            }
            catch (FileNotFoundException ex)
            {
                AssertContains(ex.Message, "SpatiaLite rebuild script was not found", "SpatiaLite repair missing script errors should localize English messages.");
            }
            finally
            {
                if (Directory.Exists(missingScriptRoot)) Directory.Delete(missingScriptRoot, true);
            }

            File.WriteAllText(
                Path.Combine(runtimeDir, "SPATIALITE_RUNTIME_MANIFEST.json"),
                "{\"source_url\":\"https://www.gaia-gis.it/gaia-sins/libspatialite-sources/libspatialite-5.1.0.zip\",\"source_sha256\":\"abc123\",\"built_at_utc\":\"2026-05-24T00:00:00Z\",\"files\":[{\"name\":\"libspatialite.dll\",\"sha256\":\"" + runtimeDllHash + "\",\"bytes\":" + new FileInfo(runtimeDllPath).Length + "}]}",
                Encoding.UTF8);
            List<SpatiaLiteDiagnosticRow> verifiedRows = SpatiaLiteRuntimeDiagnosticService.BuildRows(runtimeDir, "", root);
            string verifiedDetails = string.Join("\n", verifiedRows.Select(r => r.Item + "|" + r.Status + "|" + r.Detail).ToArray());
            AssertContains(verifiedDetails, "SpatiaLite Runtime File Verification|Ready|Verified 1 runtime file(s).", "SpatiaLite diagnostics should report ready when every manifest file matches.");

            using (my_sqlite sqlite = new my_sqlite())
            {
                string sqliteLanguage = Localization.CurrentLanguage;
                try
                {
                    Localization.SetLanguage(Localization.TraditionalChinese, false);
                    sqlite.RetryLoadSpatiaLite();
                    AssertContains(sqlite.SpatiaLiteLoadError, "尚未開啟", "Retrying SpatiaLite without an open SQLite connection should localize the error in Traditional Chinese.");

                    Localization.SetLanguage(Localization.English, false);
                    sqlite.RetryLoadSpatiaLite();
                    AssertContains(sqlite.SpatiaLiteLoadError, "not open", "Retrying SpatiaLite without an open SQLite connection should localize the error in English.");
                }
                finally
                {
                    Localization.SetLanguage(sqliteLanguage, false);
                }
            }
        }
        finally
        {
            Localization.SetLanguage(oldLanguage, false);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string ComputeFileSha256ForTest(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            byte[] hash = sha.ComputeHash(stream);
            StringBuilder builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }
    }

    private static void TestQueryResultExportService()
    {
        DataTable table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Amount", typeof(decimal));
        table.Columns.Add("Payload", typeof(byte[]));
        table.Rows.Add("A, B", 12.5m, new byte[] { 0xAA, 0xBB, 0xCC });

        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            string zhCsv = QueryResultExportService.BuildText(table, QueryResultExportFormat.Csv);
            AssertContains(zhCsv, "\"A, B\",12.5,[BLOB 3 位元組] 0xAABBCC", "CSV export should localize BLOB previews in Traditional Chinese.");

            Localization.SetLanguage(Localization.English, false);
            string csv = QueryResultExportService.BuildText(table, QueryResultExportFormat.Csv);
            AssertContains(csv, "\"A, B\",12.5,[BLOB 3 bytes] 0xAABBCC", "CSV export should escape commas and format BLOB values.");

            string json = QueryResultExportService.BuildText(table, QueryResultExportFormat.Json);
            AssertContains(json, "\"Name\": \"A, B\"", "JSON export should include string values.");
            AssertContains(json, "[BLOB 3 bytes] 0xAABBCC", "JSON export should use the shared BLOB preview.");

            string markdown = QueryResultExportService.BuildText(table, QueryResultExportFormat.Markdown);
            AssertContains(markdown, "| Name | Amount | Payload |", "Markdown export should include headers.");
        }
        finally
        {
            Localization.SetLanguage(previousLanguage, false);
        }

        string sqlScript = QueryResultExportService.BuildText(table, QueryResultExportFormat.Sql);
        AssertContains(sqlScript, "INSERT INTO \"query_result\" (\"Name\", \"Amount\", \"Payload\") VALUES ('A, B', 12.5, X'AABBCC');", "SQL export should write insert statements with escaped identifiers and BLOB literals.");
        Assert(QueryResultExportService.ResolveFormat("result.json", 2) == QueryResultExportFormat.Json, "Extension should determine query export format.");
        Assert(QueryResultExportService.ResolveFormat("result.sql", 1) == QueryResultExportFormat.Sql, "SQL extension should determine query export format.");
        Assert(QueryResultExportService.ResolveFormat("result", 1) == QueryResultExportFormat.Csv, "First export filter should default to CSV.");
        Assert(QueryResultExportService.ResolveFormat("result", 2) == QueryResultExportFormat.Xlsx, "Second export filter should still support Excel.");
        Assert(QueryResultExportService.ResolveFormat("result", 8) == QueryResultExportFormat.Sql, "SQL export filter should select SQL insert format.");
        Assert(QueryResultExportService.CanStreamFormat(QueryResultExportFormat.Sql), "SQL insert export should support streaming query results.");
        Assert(QueryResultExportService.CountExportRows(table) == 1, "Query export service should count non-deleted rows.");

        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            try
            {
                QueryResultExportService.BuildSummary("", QueryResultExportFormat.Csv, 0);
                Assert(false, "Query export summary should require a target path.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "請指定輸出目標路徑", "Query export summary should localize Traditional Chinese target path errors.");
            }

            my_sqlite validationDb = new my_sqlite();
            try
            {
                QueryResultExportService.WriteStreaming(validationDb, "", null, "result.csv", QueryResultExportFormat.Csv);
                Assert(false, "Streaming query export should require SQL text.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "請輸入 SQL", "Streaming query export should localize Traditional Chinese SQL validation errors.");
            }
            finally
            {
                validationDb.Dispose();
            }

            Localization.SetLanguage(Localization.English, false);
            try
            {
                QueryResultExportService.BuildSummary("", QueryResultExportFormat.Csv, 0);
                Assert(false, "Query export summary should require a target path in English.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "Target path is required", "Query export summary should localize English target path errors.");
            }

            try
            {
                QueryResultExportService.WriteStreaming(new my_sqlite(), "SELECT 1;", null, "result.xlsx", QueryResultExportFormat.Xlsx);
                Assert(false, "Streaming query export should reject unsupported formats.");
            }
            catch (NotSupportedException ex)
            {
                AssertContains(ex.Message, "Streaming export only supports", "Streaming query export should localize English unsupported format errors.");
            }
        }
        finally
        {
            Localization.SetLanguage(previousLanguage, false);
        }

        string xlsxPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_query_export_" + Guid.NewGuid().ToString("N") + ".xlsx");
        string summaryPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_query_export_summary_" + Guid.NewGuid().ToString("N") + ".csv");
        string csvPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_stream_export_" + Guid.NewGuid().ToString("N") + ".csv");
        string jsonPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_stream_export_" + Guid.NewGuid().ToString("N") + ".json");
        string xmlPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_stream_export_" + Guid.NewGuid().ToString("N") + ".xml");
        string htmlPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_stream_export_" + Guid.NewGuid().ToString("N") + ".html");
        string markdownPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_stream_export_" + Guid.NewGuid().ToString("N") + ".md");
        string sqlPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_stream_export_" + Guid.NewGuid().ToString("N") + ".sql");
        my_sqlite db = new my_sqlite();
        try
        {
            db.SetConn("Data Source=:memory:;Version=3;New=True;");
            db.Open();
            db.ExecSQL("CREATE TABLE export_test (id INTEGER PRIMARY KEY, name TEXT, amount REAL, payload BLOB);");
            db.ExecSQL("INSERT INTO export_test (id, name, amount, payload) VALUES (@p0, @p1, @p2, @p3);",
                new Dictionary<string, object> { { "p0", 1 }, { "p1", "A, B" }, { "p2", 12.5 }, { "p3", new byte[] { 0xAA, 0xBB, 0xCC } } });
            db.ExecSQL("INSERT INTO export_test (id, name, amount, payload) VALUES (@p0, @p1, @p2, @p3);",
                new Dictionary<string, object> { { "p0", 2 }, { "p1", "Line\nBreak" }, { "p2", 7.25 }, { "p3", DBNull.Value } });

            QueryResultExportService.Write(table, xlsxPath, QueryResultExportFormat.Xlsx);
            using (ZipArchive archive = ZipFile.OpenRead(xlsxPath))
            {
                string sheetXml = ReadZipEntryText(archive, "xl/worksheets/sheet1.xml");
                string stylesXml = ReadZipEntryText(archive, "xl/styles.xml");
                AssertContains(sheetXml, "<pane ySplit=\"1\" topLeftCell=\"A2\"", "XLSX export should freeze the header row.");
                AssertContains(sheetXml, "<autoFilter ref=\"A1:C2\"", "XLSX export should enable filters across exported columns.");
                AssertContains(sheetXml, "<cols><col min=\"1\" max=\"1\"", "XLSX export should include stable column widths.");
                AssertContains(sheetXml, "r=\"A1\" t=\"inlineStr\" s=\"1\"", "XLSX export should apply the header style.");
                AssertContains(stylesXml, "<fonts count=\"2\">", "XLSX export should include a header font style.");
                AssertContains(stylesXml, "<cellXfs count=\"2\">", "XLSX export should include a header cell format.");
            }

            File.WriteAllText(summaryPath, "Name,Amount\r\nA,12.5\r\n", Encoding.UTF8);
            QueryResultExportSummary summary = QueryResultExportService.BuildSummary(summaryPath, QueryResultExportFormat.Csv, 1);
            AssertEquals(summaryPath, summary.Path, "Export summary should keep the target path.");
            AssertEquals(Path.GetFileName(summaryPath), summary.FileName, "Export summary should expose the file name.");
            AssertEquals(Path.GetDirectoryName(summaryPath), summary.DirectoryPath, "Export summary should expose the target directory.");
            Assert(summary.Rows == 1, "Export summary should keep the exported row count.");
            Assert(summary.BytesWritten > 0, "Export summary should read the written file size.");
            AssertEquals("CSV", summary.FormatName, "Export summary should expose a friendly format name.");
            AssertContains(summary.BuildDetailText(), summary.FileName, "Export summary detail text should include the file name.");
            AssertContains(summary.BuildDetailText(), "CSV", "Export summary detail text should include the format name.");
            try
            {
                Localization.SetLanguage(Localization.TraditionalChinese, false);
                string zhSummaryDetail = summary.BuildDetailText();
                AssertContains(zhSummaryDetail, "格式： CSV", "Export summary detail text should localize Traditional Chinese labels.");
                AssertContains(zhSummaryDetail, "列數： 1", "Export summary detail text should include localized row count.");
                Assert(!zhSummaryDetail.Contains("Format:"), "Traditional Chinese export summary detail should not keep hardcoded English labels.");
                MethodInfo openFailedMethod = typeof(ExportCompletedDialog).GetMethod("BuildOpenTargetFailedMessage", BindingFlags.Static | BindingFlags.NonPublic);
                string zhOpenFailedMessage = (string)openFailedMethod.Invoke(null, new object[] { new Exception("") });
                AssertContains(zhOpenFailedMessage, "開啟匯出目標失敗：未知錯誤", "Export completed dialog should localize empty open-target errors in Traditional Chinese.");

                Localization.SetLanguage(Localization.English, false);
                string enSummaryDetail = summary.BuildDetailText();
                AssertContains(enSummaryDetail, "Format: CSV", "Export summary detail text should support English labels.");
                AssertContains(enSummaryDetail, "Rows: 1", "English export summary detail should include row count.");
                string enOpenFailedMessage = (string)openFailedMethod.Invoke(null, new object[] { new Exception("") });
                AssertContains(enOpenFailedMessage, "Failed to open export target: Unknown error", "Export completed dialog should localize empty open-target errors in English.");
            }
            finally
            {
                Localization.SetLanguage(previousLanguage, false);
            }
            using (ExportCompletedDialog dialog = new ExportCompletedDialog(summary))
            {
                AssertEquals(Localization.T("Query.ExportSummaryTitle"), dialog.Text, "Export completed dialog should use the localized title.");
                Assert(dialog.Controls.Count > 0, "Export completed dialog should initialize its layout.");
            }

            Localization.SetLanguage(Localization.English, false);
            long lastProgress = 0;
            QueryResultStreamingExportResult streamCsv = QueryResultExportService.WriteStreaming(
                db,
                "SELECT name, amount, payload FROM export_test WHERE id >= @p0 ORDER BY id;",
                new Dictionary<string, object> { { "p0", 1 } },
                csvPath,
                QueryResultExportFormat.Csv,
                rows => lastProgress = rows);
            string streamedCsv = File.ReadAllText(csvPath, Encoding.UTF8);
            Assert(streamCsv.Rows == 2 && lastProgress == 2, "Streaming CSV export should report exported rows.");
            Assert(streamCsv.BytesWritten > 0, "Streaming CSV export should report written bytes.");
            AssertContains(streamedCsv, "name,amount,payload", "Streaming CSV export should include headers.");
            AssertContains(streamedCsv, "\"A, B\",12.5,[BLOB 3 bytes] 0xAABBCC", "Streaming CSV export should escape values and format BLOB previews.");
            AssertContains(streamedCsv, "\"Line\nBreak\",7.25,", "Streaming CSV export should preserve embedded newlines safely.");

            QueryResultStreamingExportResult streamJson = QueryResultExportService.WriteStreaming(
                db,
                "SELECT name, payload FROM export_test ORDER BY id;",
                null,
                jsonPath,
                QueryResultExportFormat.Json);
            string streamedJson = File.ReadAllText(jsonPath, Encoding.UTF8);
            Assert(streamJson.Rows == 2, "Streaming JSON export should report exported rows.");
            AssertContains(streamedJson, "\"name\":\"A, B\"", "Streaming JSON export should include row objects without loading a DataTable.");
            AssertContains(streamedJson, "\"payload\":\"[BLOB 3 bytes] 0xAABBCC\"", "Streaming JSON export should use shared BLOB previews.");
            AssertContains(streamedJson, "\"payload\":null", "Streaming JSON export should preserve null values.");

            QueryResultStreamingExportResult streamXml = QueryResultExportService.WriteStreaming(
                db,
                "SELECT name, payload FROM export_test ORDER BY id;",
                null,
                xmlPath,
                QueryResultExportFormat.Xml);
            string streamedXml = File.ReadAllText(xmlPath, Encoding.UTF8);
            Assert(streamXml.Rows == 2, "Streaming XML export should report exported rows.");
            AssertContains(streamedXml, "<?xml version=\"1.0\" encoding=\"utf-8\"?>", "Streaming XML export should include a document header.");
            AssertContains(streamedXml, "<field name=\"name\">A, B</field>", "Streaming XML export should include field values.");
            AssertContains(streamedXml, "<field name=\"payload\" isNull=\"true\" />", "Streaming XML export should preserve null values.");

            QueryResultStreamingExportResult streamHtml = QueryResultExportService.WriteStreaming(
                db,
                "SELECT name, payload FROM export_test ORDER BY id;",
                null,
                htmlPath,
                QueryResultExportFormat.Html);
            string streamedHtml = File.ReadAllText(htmlPath, Encoding.UTF8);
            Assert(streamHtml.Rows == 2, "Streaming HTML export should report exported rows.");
            AssertContains(streamedHtml, "<table>", "Streaming HTML export should include a table.");
            AssertContains(streamedHtml, "<td>A, B</td>", "Streaming HTML export should include escaped table cells.");
            AssertContains(streamedHtml, "<td>[BLOB 3 bytes] 0xAABBCC</td>", "Streaming HTML export should use shared BLOB previews.");

            QueryResultStreamingExportResult streamMarkdown = QueryResultExportService.WriteStreaming(
                db,
                "SELECT name, payload FROM export_test ORDER BY id;",
                null,
                markdownPath,
                QueryResultExportFormat.Markdown);
            string streamedMarkdown = File.ReadAllText(markdownPath, Encoding.UTF8);
            Assert(streamMarkdown.Rows == 2, "Streaming Markdown export should report exported rows.");
            AssertContains(streamedMarkdown, "| name | payload |", "Streaming Markdown export should include headers.");
            AssertContains(streamedMarkdown, "| A, B | [BLOB 3 bytes] 0xAABBCC |", "Streaming Markdown export should include row values.");
            AssertContains(streamedMarkdown, "| Line<br>Break |  |", "Streaming Markdown export should keep multiline cells table-safe.");

            QueryResultStreamingExportResult streamSql = QueryResultExportService.WriteStreaming(
                db,
                "SELECT name, amount, payload FROM export_test ORDER BY id;",
                null,
                sqlPath,
                QueryResultExportFormat.Sql);
            string streamedSql = File.ReadAllText(sqlPath, Encoding.UTF8);
            Assert(streamSql.Rows == 2, "Streaming SQL export should report exported rows.");
            AssertContains(streamedSql, "INSERT INTO \"query_result\" (\"name\", \"amount\", \"payload\") VALUES ('A, B', 12.5, X'AABBCC');", "Streaming SQL export should write insert statements.");
            AssertContains(streamedSql, "INSERT INTO \"query_result\" (\"name\", \"amount\", \"payload\") VALUES ('Line", "Streaming SQL export should preserve multiline string values.");
            AssertContains(streamedSql, ", 7.25, NULL);", "Streaming SQL export should preserve null values.");
        }
        finally
        {
            Localization.SetLanguage(previousLanguage, false);
            db.Dispose();
            if (File.Exists(xlsxPath)) File.Delete(xlsxPath);
            if (File.Exists(summaryPath)) File.Delete(summaryPath);
            if (File.Exists(csvPath)) File.Delete(csvPath);
            if (File.Exists(jsonPath)) File.Delete(jsonPath);
            if (File.Exists(xmlPath)) File.Delete(xmlPath);
            if (File.Exists(htmlPath)) File.Delete(htmlPath);
            if (File.Exists(markdownPath)) File.Delete(markdownPath);
            if (File.Exists(sqlPath)) File.Delete(sqlPath);
        }

        QueryForm form = (QueryForm)FormatterServices.GetUninitializedObject(typeof(QueryForm));
        SetPrivateField(form, "_lastResultSql", "SELECT name FROM export_test ORDER BY id;");
        SetPrivateField(form, "_lastResultCanStreamExport", true);
        SetPrivateField(form, "_isTableDataMode", false);
        string streamingSql;
        Assert(TryGetQueryFormStreamingExportSql(form, QueryResultExportFormat.Csv, out streamingSql),
            "Query form should enable streaming export for successful non-table CSV results.");
        AssertEquals("SELECT name FROM export_test ORDER BY id;", streamingSql, "Query form should stream the last successful result SQL.");
        Assert(TryGetQueryFormStreamingExportSql(form, QueryResultExportFormat.Json, out streamingSql),
            "Query form should enable streaming export for JSON results.");
        Assert(TryGetQueryFormStreamingExportSql(form, QueryResultExportFormat.Xml, out streamingSql),
            "Query form should enable streaming export for XML results.");
        Assert(TryGetQueryFormStreamingExportSql(form, QueryResultExportFormat.Html, out streamingSql),
            "Query form should enable streaming export for HTML results.");
        Assert(TryGetQueryFormStreamingExportSql(form, QueryResultExportFormat.Markdown, out streamingSql),
            "Query form should enable streaming export for Markdown results.");
        Assert(!TryGetQueryFormStreamingExportSql(form, QueryResultExportFormat.Xlsx, out streamingSql),
            "Query form should keep formatted workbook exports on the DataTable path.");
        SetPrivateField(form, "_isTableDataMode", true);
        Assert(!TryGetQueryFormStreamingExportSql(form, QueryResultExportFormat.Csv, out streamingSql),
            "Query form should not re-query table edit mode because current rows may contain unsaved edits.");
    }

    private static void TestQueryFormOptionSettings()
    {
        bool oldRecordLimitEnabled = ApplicationOptionSettings.GetBool("RecordLimitEnabled");
        int oldRecordLimit = ApplicationOptionSettings.GetInt("RecordLimit");
        string oldEditorFontName = ApplicationOptionSettings.GetString("EditorFontName");
        int oldEditorFontSize = ApplicationOptionSettings.GetInt("EditorFontSize");
        bool oldEditorWordWrap = ApplicationOptionSettings.GetBool("EditorWordWrap");
        int oldEditorLargeFileLimitMb = ApplicationOptionSettings.GetInt("EditorLargeFileLimitMb");
        bool oldAutoCompleteEnabled = ApplicationOptionSettings.GetBool("AutoCompleteEnabled");
        bool oldShowObjectTooltips = ApplicationOptionSettings.GetBool("ShowObjectTooltips");
        bool oldRecordAutoBeginTransaction = ApplicationOptionSettings.GetBool("RecordAutoBeginTransaction");
        string oldGridFontName = ApplicationOptionSettings.GetString("RecordGridFontName");
        int oldGridFontSize = ApplicationOptionSettings.GetInt("RecordGridFontSize");
        string oldDateFormat = ApplicationOptionSettings.GetString("RecordDateFormat");
        string oldTimeFormat = ApplicationOptionSettings.GetString("RecordTimeFormat");
        string oldDateTimeFormat = ApplicationOptionSettings.GetString("RecordDateTimeFormat");
        bool oldShowThousands = ApplicationOptionSettings.GetBool("RecordShowThousandsSeparator");
        bool oldUseSystemNumberFormat = ApplicationOptionSettings.GetBool("RecordUseSystemNumberFormat");
        string oldRowHeightMode = ApplicationOptionSettings.GetString("RecordRowHeightMode");
        string oldExportDirectory = ApplicationOptionSettings.GetString("FileExportDirectory");
        string oldLanguage = Localization.CurrentLanguage;

        try
        {
            ApplicationOptionSettings.SetBool("RecordLimitEnabled", true);
            ApplicationOptionSettings.SetInt("RecordLimit", 321);
            ApplicationOptionSettings.SetString("EditorFontName", "Consolas");
            ApplicationOptionSettings.SetInt("EditorFontSize", 13);
            ApplicationOptionSettings.SetBool("EditorWordWrap", false);
            ApplicationOptionSettings.SetInt("EditorLargeFileLimitMb", 1);
            ApplicationOptionSettings.SetBool("AutoCompleteEnabled", false);
            ApplicationOptionSettings.SetBool("ShowObjectTooltips", true);
            ApplicationOptionSettings.SetBool("RecordAutoBeginTransaction", true);
            ApplicationOptionSettings.SetString("RecordGridFontName", "Consolas");
            ApplicationOptionSettings.SetInt("RecordGridFontSize", 12);
            ApplicationOptionSettings.SetString("RecordDateFormat", "yyyy/MM/dd");
            ApplicationOptionSettings.SetString("RecordTimeFormat", "HH:mm:ss");
            ApplicationOptionSettings.SetString("RecordDateTimeFormat", "yyyy/MM/dd HH:mm:ss");
            ApplicationOptionSettings.SetBool("RecordShowThousandsSeparator", true);
            ApplicationOptionSettings.SetBool("RecordUseSystemNumberFormat", false);
            ApplicationOptionSettings.SetString("RecordRowHeightMode", "comfortable");

            Localization.SetLanguage(Localization.TraditionalChinese, false);
            MethodInfo queryExceptionMessageMethod = typeof(QueryForm).GetMethod("BuildQueryExceptionMessage", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo queryFailureReasonMethod = typeof(QueryForm).GetMethod("BuildQueryFailureReason", BindingFlags.Static | BindingFlags.NonPublic);
            AssertEquals("未知錯誤", (string)queryExceptionMessageMethod.Invoke(null, new object[] { new Exception("") }), "Query form empty exceptions should localize Traditional Chinese unknown errors.");
            AssertEquals("provider timeout", (string)queryFailureReasonMethod.Invoke(null, new object[] { "provider timeout" }), "Query form failure reason should preserve provider messages.");
            Localization.SetLanguage(Localization.English, false);
            AssertEquals("Unknown error", (string)queryFailureReasonMethod.Invoke(null, new object[] { "   " }), "Query form blank provider reasons should localize English unknown errors.");
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            using (QueryForm form = new QueryForm(new FakeDumpDatabase(), "main"))
            {
                ToolStripTextBox pageSize = GetPrivateField<ToolStripTextBox>(form, "txtPageSize");
                RichTextBox editor = GetPrivateField<RichTextBox>(form, "txtSql");
                DataGridView grid = GetPrivateField<DataGridView>(form, "dgvResults");
                List<string> completionTables = GetPrivateField<List<string>>(form, "_tableNames");
                ToolStripButton addButton = GetPrivateField<ToolStripButton>(form, "btnDataAdd");
                ToolStripButton applyButton = GetPrivateField<ToolStripButton>(form, "btnDataApply");
                ToolStripButton nextButton = GetPrivateField<ToolStripButton>(form, "btnDataNext");

                AssertEquals("321", pageSize.Text, "Query form should use the configured record limit.");
                Assert(!editor.WordWrap, "Query editor should honor the configured word wrap setting.");
                Assert(Math.Abs(editor.Font.SizeInPoints - 13f) < 0.1f, "Query editor should honor the configured font size.");
                Assert(Math.Abs(grid.DefaultCellStyle.Font.SizeInPoints - 12f) < 0.1f, "Result grid should honor the configured font size.");
                Assert(grid.AutoSizeRowsMode == DataGridViewAutoSizeRowsMode.None, "Result grid should disable automatic row resizing when row height is configured.");
                Assert(grid.RowTemplate.Height >= 32, "Result grid should honor the comfortable row height setting.");
                Assert(completionTables.Count == 0, "Disabled auto-complete should skip metadata loading.");
                AssertEquals("新增資料列", addButton.ToolTipText, "Data toolbar add tooltip should localize Traditional Chinese.");
                AssertEquals("儲存資料變更", applyButton.ToolTipText, "Data toolbar save tooltip should localize Traditional Chinese.");
                AssertEquals("下一頁", nextButton.ToolTipText, "Data toolbar pagination tooltip should localize Traditional Chinese.");

                Localization.SetLanguage(Localization.English, false);
                form.ApplyLanguage();
                AssertEquals("Add row", addButton.ToolTipText, "Data toolbar add tooltip should support English.");
                AssertEquals("Save data changes", applyButton.ToolTipText, "Data toolbar save tooltip should support English.");
                AssertEquals("Next page", nextButton.ToolTipText, "Data toolbar pagination tooltip should support English.");

                ApplicationOptionSettings.SetBool("ShowObjectTooltips", false);
                form.ApplyLanguage();
                AssertEquals("", addButton.ToolTipText, "Data toolbar tooltips should be cleared when tooltips are disabled.");
                AssertEquals("", nextButton.ToolTipText, "Data toolbar pagination tooltips should be cleared when tooltips are disabled.");
                ApplicationOptionSettings.SetBool("ShowObjectTooltips", true);
            }

            ApplicationOptionSettings.SetString("RecordRowHeightMode", "compact");
            using (Font rowHeightFont = new Font("Consolas", 12))
            {
                int compactHeight = GetConfiguredQueryResultRowHeight(rowHeightFont);
                ApplicationOptionSettings.SetString("RecordRowHeightMode", "comfortable");
                int comfortableHeight = GetConfiguredQueryResultRowHeight(rowHeightFont);
                Assert(compactHeight < comfortableHeight, "Compact row height should be smaller than comfortable row height.");
            }

            AssertEquals("2026/05/20", FormatQueryResultValue(new DateTime(2026, 5, 20)), "Query result date formatting should honor RecordDateFormat.");
            AssertEquals("2026/05/20 13:45:06", FormatQueryResultValue(new DateTime(2026, 5, 20, 13, 45, 6)), "Query result datetime formatting should honor RecordDateTimeFormat.");
            AssertEquals("03:04:05", FormatQueryResultValue(new TimeSpan(3, 4, 5)), "Query result time formatting should honor RecordTimeFormat.");
            AssertEquals("1,234.56", FormatQueryResultValue(1234.56m), "Query result numeric formatting should honor thousands and invariant number options.");
            AssertEquals("9,876", FormatQueryResultValue(9876), "Query result integer formatting should honor thousands option.");
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            AssertEquals("[BLOB 3 位元組] 0xAABBCC", FormatQueryResultValue(new byte[] { 0xAA, 0xBB, 0xCC }), "Query result binary previews should localize Traditional Chinese units.");
            AssertEquals("[BLOB 3 位元組] 0xAABBCC", FormatMainBinaryGridValue(new byte[] { 0xAA, 0xBB, 0xCC }), "Main grid binary previews should localize Traditional Chinese units.");
            AssertEquals("[BLOB 3 位元組]", FormatQueryConflictParameterValue(new byte[] { 0xAA, 0xBB, 0xCC }), "Conflict binary previews should localize Traditional Chinese units.");
            Localization.SetLanguage(Localization.English, false);
            AssertEquals("[BLOB 3 bytes] 0xAABBCC", FormatQueryResultValue(new byte[] { 0xAA, 0xBB, 0xCC }), "Query result binary previews should support English units.");
            AssertEquals("[BLOB 3 bytes] 0xAABBCC", FormatMainBinaryGridValue(new byte[] { 0xAA, 0xBB, 0xCC }), "Main grid binary previews should support English units.");
            AssertEquals("[BLOB 3 bytes]", FormatQueryConflictParameterValue(new byte[] { 0xAA, 0xBB, 0xCC }), "Conflict binary previews should support English units.");
            Assert(QueryEditorHelpersEnabled("SELECT 1"), "Editor helpers should remain enabled below the large SQL limit.");
            Assert(!QueryEditorHelpersEnabled(new string('x', 1024 * 1024 + 1)), "Editor helpers should be disabled above the configured large SQL limit.");

            using (ToolStripButton tooltipButton = new ToolStripButton("Demo"))
            {
                ApplyObjectTooltipForTest(tooltipButton, "Demo tooltip");
                AssertEquals("Demo tooltip", tooltipButton.ToolTipText, "Object tooltips should be applied when the option is enabled.");
                ApplicationOptionSettings.SetBool("ShowObjectTooltips", false);
                ApplyObjectTooltipForTest(tooltipButton, "Demo tooltip");
                AssertEquals("", tooltipButton.ToolTipText, "Object tooltips should be cleared when the option is disabled.");
            }

            AssertEquals("START TRANSACTION;", GetTableSaveTransactionSql("mysql", "BeginSql"), "MySQL table saves should use START TRANSACTION.");
            AssertEquals("BEGIN;", GetTableSaveTransactionSql("postgres", "BeginSql"), "PostgreSQL alias should use BEGIN.");
            AssertEquals("BEGIN TRANSACTION;", GetTableSaveTransactionSql("sqlite", "BeginSql"), "SQLite table saves should use BEGIN TRANSACTION.");
            AssertEquals("COMMIT TRANSACTION;", GetTableSaveTransactionSql("sqlserver", "CommitSql"), "SQL Server alias should use COMMIT TRANSACTION.");
            AssertEquals("ROLLBACK", GetTableSaveTransactionSql("oracle", "RollbackSql"), "Oracle table saves should use ROLLBACK without an explicit BEGIN.");
            ApplicationOptionSettings.SetBool("RecordAutoBeginTransaction", false);
            AssertEquals("", GetTableSaveTransactionSql("mysql", "BeginSql"), "Transaction statements should be disabled when the option is off.");

            string rememberedExportDirectory = Path.Combine(Path.GetTempPath(), "mysqlpunk_export_remember_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rememberedExportDirectory);
            RememberQueryFormConfiguredDirectory("FileExportDirectory", Path.Combine(rememberedExportDirectory, "result.csv"));
            AssertEquals(rememberedExportDirectory, ApplicationOptionSettings.GetString("FileExportDirectory"), "Query export should remember the selected output folder.");
            RememberQueryFormConfiguredDirectory("FileExportDirectory", "");
            AssertEquals(rememberedExportDirectory, ApplicationOptionSettings.GetString("FileExportDirectory"), "Empty export paths should not clear the remembered folder.");
            Directory.Delete(rememberedExportDirectory);

            Localization.SetLanguage(Localization.TraditionalChinese, false);
            AssertEquals("自動完成資料已清除。", OptionsForm.BuildAutoCompleteCacheClearedMessage(), "Auto-complete cache clear message should localize Traditional Chinese text.");
            Localization.SetLanguage(Localization.English, false);
            AssertEquals("Completion data cleared.", OptionsForm.BuildAutoCompleteCacheClearedMessage(), "Auto-complete cache clear message should localize English text.");

            string cachePath = Path.Combine(Application.UserAppDataPath, "autocomplete-cache.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
            File.WriteAllText(cachePath, "{}", Encoding.UTF8);
            ApplicationOptionSettings.ClearAutoCompleteCache();
            Assert(!File.Exists(cachePath), "ClearAutoCompleteCache should delete the auto-complete cache file.");
        }
        finally
        {
            Localization.SetLanguage(oldLanguage, false);
            ApplicationOptionSettings.SetBool("RecordLimitEnabled", oldRecordLimitEnabled);
            ApplicationOptionSettings.SetInt("RecordLimit", oldRecordLimit);
            ApplicationOptionSettings.SetString("EditorFontName", oldEditorFontName);
            ApplicationOptionSettings.SetInt("EditorFontSize", oldEditorFontSize);
            ApplicationOptionSettings.SetBool("EditorWordWrap", oldEditorWordWrap);
            ApplicationOptionSettings.SetInt("EditorLargeFileLimitMb", oldEditorLargeFileLimitMb);
            ApplicationOptionSettings.SetBool("AutoCompleteEnabled", oldAutoCompleteEnabled);
            ApplicationOptionSettings.SetBool("ShowObjectTooltips", oldShowObjectTooltips);
            ApplicationOptionSettings.SetBool("RecordAutoBeginTransaction", oldRecordAutoBeginTransaction);
            ApplicationOptionSettings.SetString("RecordGridFontName", oldGridFontName);
            ApplicationOptionSettings.SetInt("RecordGridFontSize", oldGridFontSize);
            ApplicationOptionSettings.SetString("RecordDateFormat", oldDateFormat);
            ApplicationOptionSettings.SetString("RecordTimeFormat", oldTimeFormat);
            ApplicationOptionSettings.SetString("RecordDateTimeFormat", oldDateTimeFormat);
            ApplicationOptionSettings.SetBool("RecordShowThousandsSeparator", oldShowThousands);
            ApplicationOptionSettings.SetBool("RecordUseSystemNumberFormat", oldUseSystemNumberFormat);
            ApplicationOptionSettings.SetString("RecordRowHeightMode", oldRowHeightMode);
            ApplicationOptionSettings.SetString("FileExportDirectory", oldExportDirectory);
            ApplicationOptionSettings.Save();
        }
    }

    private static void RememberQueryFormConfiguredDirectory(string optionKey, string filePath)
    {
        MethodInfo method = typeof(QueryForm).GetMethod("RememberConfiguredDirectoryForPath", BindingFlags.Static | BindingFlags.NonPublic);
        method.Invoke(null, new object[] { optionKey, filePath });
    }

    private static string GetTableSaveTransactionSql(string providerName, string propertyName)
    {
        MethodInfo method = typeof(QueryForm).GetMethod("BuildTableSaveTransactionStatements", BindingFlags.Static | BindingFlags.NonPublic);
        object statements = method.Invoke(null, new object[] { new FakeExecDatabase(providerName, "1") });
        if (statements == null) return "";
        return Convert.ToString(statements.GetType().GetProperty(propertyName).GetValue(statements, null));
    }

    private static void ApplyObjectTooltipForTest(ToolStripItem item, string text)
    {
        MethodInfo method = typeof(Form1).GetMethod("ApplyObjectTooltip", BindingFlags.Static | BindingFlags.NonPublic);
        method.Invoke(null, new object[] { item, text });
    }

    private static bool QueryEditorHelpersEnabled(string sql)
    {
        MethodInfo method = typeof(QueryForm).GetMethod("ShouldUseEditorHelpers", BindingFlags.Static | BindingFlags.NonPublic);
        return (bool)method.Invoke(null, new object[] { sql });
    }

    private static int GetConfiguredQueryResultRowHeight(Font font)
    {
        MethodInfo method = typeof(QueryForm).GetMethod("GetConfiguredResultGridRowHeight", BindingFlags.Static | BindingFlags.NonPublic);
        return (int)method.Invoke(null, new object[] { font });
    }

    private static string FormatQueryResultValue(object value)
    {
        MethodInfo method = typeof(QueryForm).GetMethod("TryFormatConfiguredResultCellValue", BindingFlags.Static | BindingFlags.NonPublic);
        object[] args = new object[] { value, null };
        bool applied = (bool)method.Invoke(null, args);
        Assert(applied, "Query result value should be formatted.");
        return Convert.ToString(args[1]);
    }

    private static string FormatMainBinaryGridValue(byte[] value)
    {
        MethodInfo method = typeof(Form1).GetMethod("FormatBinaryGridCellValue", BindingFlags.Static | BindingFlags.NonPublic);
        return (string)method.Invoke(null, new object[] { value });
    }

    private static string FormatQueryConflictParameterValue(object value)
    {
        MethodInfo method = typeof(QueryForm).GetMethod("FormatConflictParameterValue", BindingFlags.Static | BindingFlags.NonPublic);
        return (string)method.Invoke(null, new object[] { value });
    }

    private static void TestQueryTableEditOptimisticWhere()
    {
        DataTable table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("note", typeof(string));
        table.Columns.Add("payload", typeof(byte[]));
        table.Rows.Add(7, "old name", DBNull.Value, new byte[] { 0x01, 0x02 });
        table.AcceptChanges();
        DataRow row = table.Rows[0];
        row["name"] = "new name";

        QueryForm form = (QueryForm)FormatterServices.GetUninitializedObject(typeof(QueryForm));
        SetPrivateField(form, "_db", new FakeDumpDatabase());

        Dictionary<string, object> primaryParameters = new Dictionary<string, object>();
        int primaryIndex = 0;
        string primaryWhere = BuildQueryFormWhereClause(
            form,
            row,
            new[]
            {
                CreateQueryFormColumnInfo("id", true),
                CreateQueryFormColumnInfo("name", false),
                CreateQueryFormColumnInfo("payload", false)
            },
            new List<string> { "id" },
            primaryParameters,
            ref primaryIndex);
        AssertEquals("\"id\" = :p0", primaryWhere, "Primary-key edit WHERE should only target the primary key.");
        Assert(primaryParameters.Count == 1 && object.Equals(primaryParameters["p0"], 7), "Primary-key edit WHERE should bind the original key value.");

        Dictionary<string, object> optimisticParameters = new Dictionary<string, object>();
        int optimisticIndex = 0;
        string optimisticWhere = BuildQueryFormWhereClause(
            form,
            row,
            new[]
            {
                CreateQueryFormColumnInfo("id", false),
                CreateQueryFormColumnInfo("name", false),
                CreateQueryFormColumnInfo("note", false),
                CreateQueryFormColumnInfo("payload", false)
            },
            new List<string>(),
            optimisticParameters,
            ref optimisticIndex);
        AssertContains(optimisticWhere, "\"id\" = :p0", "No-primary-key edit WHERE should include comparable original values.");
        AssertContains(optimisticWhere, "\"name\" = :p1", "No-primary-key edit WHERE should bind the original text value.");
        AssertContains(optimisticWhere, "\"note\" IS NULL", "No-primary-key edit WHERE should keep NULL comparison semantics.");
        AssertNotContains(optimisticWhere, "payload", "No-primary-key edit WHERE should skip BLOB values.");
        AssertEquals("old name", (string)optimisticParameters["p1"], "No-primary-key edit WHERE should use the original value for optimistic locking.");

        DataTable blobOnly = new DataTable();
        blobOnly.Columns.Add("payload", typeof(byte[]));
        blobOnly.Rows.Add(new byte[] { 0x01 });
        blobOnly.AcceptChanges();
        DataRow blobRow = blobOnly.Rows[0];
        blobRow["payload"] = new byte[] { 0x02 };

        bool rejectedUnsafeWhere = false;
        try
        {
            Dictionary<string, object> blobParameters = new Dictionary<string, object>();
            int blobIndex = 0;
            BuildQueryFormWhereClause(
                form,
                blobRow,
                new[] { CreateQueryFormColumnInfo("payload", false) },
                new List<string>(),
                blobParameters,
                ref blobIndex);
        }
        catch (TargetInvocationException ex)
        {
            rejectedUnsafeWhere = ex.InnerException != null &&
                ex.InnerException.Message.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        Assert(rejectedUnsafeWhere, "No-primary-key edit WHERE should reject BLOB-only optimistic matching.");

        FakeExecDatabase conflictDb = new FakeExecDatabase("postgresql", "0");
        SetPrivateField(form, "_db", conflictDb);
        bool rejectedConflict = false;
        string conflictMessage = "";
        try
        {
            InvokeQueryFormExecOrThrow(
                form,
                "UPDATE public.users SET name = :p0 WHERE id = :p1;",
                new Dictionary<string, object> { { "p0", "new name" }, { "p1", 7 } },
                true);
        }
        catch (TargetInvocationException ex)
        {
            conflictMessage = ex.InnerException != null ? ex.InnerException.Message : "";
        }
        rejectedConflict =
            conflictMessage.IndexOf("UPDATE", StringComparison.OrdinalIgnoreCase) >= 0 &&
            conflictMessage.IndexOf("WHERE id = :p1", StringComparison.OrdinalIgnoreCase) >= 0 &&
            conflictMessage.IndexOf("p0=new name", StringComparison.OrdinalIgnoreCase) >= 0 &&
            conflictMessage.IndexOf("p1=7", StringComparison.OrdinalIgnoreCase) >= 0;
        Assert(rejectedConflict, "Update/delete zero affected rows conflict should include operation, WHERE detail, and parameter values.");

        DataTable databaseCurrentRow = table.Clone();
        databaseCurrentRow.Rows.Add(7, "database name", DBNull.Value, new byte[] { 0x01, 0x02 });
        FakeExecDatabase noPrimaryConflictDb = new FakeExecDatabase("postgresql", "0");
        noPrimaryConflictDb.SelectResult = databaseCurrentRow;
        SetPrivateField(form, "_db", noPrimaryConflictDb);
        SetPrivateField(form, "_databaseName", "main");
        string databaseDiffMessage = "";
        try
        {
            InvokeQueryFormExecuteUpdate(
                form,
                "users",
                new[]
                {
                    CreateQueryFormColumnInfo("id", false),
                    CreateQueryFormColumnInfo("name", false),
                    CreateQueryFormColumnInfo("note", false),
                    CreateQueryFormColumnInfo("payload", false)
                },
                new List<string>(),
                row);
        }
        catch (TargetInvocationException ex)
        {
            databaseDiffMessage = ex.InnerException != null ? ex.InnerException.Message : "";
        }

        AssertContains(databaseDiffMessage, "資料庫目前差異", "No-primary-key update conflict should try to include the current database row diff.");
        AssertContains(databaseDiffMessage, "name", "No-primary-key conflict diff should include the changed column.");
        AssertContains(databaseDiffMessage, "old name", "No-primary-key conflict diff should include the original value.");
        AssertContains(databaseDiffMessage, "database name", "No-primary-key conflict diff should include the current database value.");
        Assert(noPrimaryConflictDb.SelectedSql.Count == 1 &&
            noPrimaryConflictDb.SelectedSql[0].IndexOf("\"id\" = :p0", StringComparison.OrdinalIgnoreCase) >= 0 &&
            noPrimaryConflictDb.SelectedSql[0].IndexOf("\"note\" IS NULL", StringComparison.OrdinalIgnoreCase) >= 0 &&
            noPrimaryConflictDb.SelectedSql[0].IndexOf("\"name\" = ", StringComparison.OrdinalIgnoreCase) < 0,
            "No-primary-key conflict re-query should use only unchanged comparable columns.");

        DataTable duplicateCurrentRows = table.Clone();
        duplicateCurrentRows.Rows.Add(7, "database name", DBNull.Value, new byte[] { 0x01 });
        duplicateCurrentRows.Rows.Add(7, "another name", DBNull.Value, new byte[] { 0x02 });
        FakeExecDatabase duplicateConflictDb = new FakeExecDatabase("postgresql", "0");
        duplicateConflictDb.SelectResult = duplicateCurrentRows;
        SetPrivateField(form, "_db", duplicateConflictDb);
        string duplicateConflictMessage = "";
        try
        {
            InvokeQueryFormExecuteUpdate(
                form,
                "users",
                new[]
                {
                    CreateQueryFormColumnInfo("id", false),
                    CreateQueryFormColumnInfo("name", false),
                    CreateQueryFormColumnInfo("note", false),
                    CreateQueryFormColumnInfo("payload", false)
                },
                new List<string>(),
                row);
        }
        catch (TargetInvocationException ex)
        {
            duplicateConflictMessage = ex.InnerException != null ? ex.InnerException.Message : "";
        }

        AssertContains(duplicateConflictMessage, "多筆候選資料列", "No-primary-key conflict diff should not guess when re-query finds multiple rows.");

        FakeExecDatabase insertDb = new FakeExecDatabase("postgresql", "0");
        SetPrivateField(form, "_db", insertDb);
        InvokeQueryFormExecOrThrow(form, "INSERT INTO public.users (name) VALUES (:p0);", new Dictionary<string, object>(), false);
        Assert(insertDb.ExecutedSql.Count == 1, "Insert should not require affected row validation.");

        string sqlitePath = Path.Combine(Path.GetTempPath(), "mysqlpunk_rows_affected_" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (my_sqlite sqliteDb = new my_sqlite())
            {
                sqliteDb.SetConn("Data Source=" + sqlitePath + ";Version=3;New=True;");
                sqliteDb.Open();
                sqliteDb.ExecSQL("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT);");
                Dictionary<string, string> insertResult = sqliteDb.ExecSQL("INSERT INTO users (id, name) VALUES (@p0, @p1);",
                    new Dictionary<string, object> { { "p0", 1 }, { "p1", "Alice" } });
                AssertEquals("1", insertResult["rowsAffected"], "SQLite insert should report affected row count.");
                Dictionary<string, string> updateMissResult = sqliteDb.ExecSQL("UPDATE users SET name = @p0 WHERE id = @p1;",
                    new Dictionary<string, object> { { "p0", "Bob" }, { "p1", 404 } });
                AssertEquals("0", updateMissResult["rowsAffected"], "SQLite update miss should report zero affected rows.");
            }
        }
        finally
        {
            if (File.Exists(sqlitePath)) File.Delete(sqlitePath);
        }
    }

    private static string BuildQueryFormWhereClause(
        QueryForm form,
        DataRow row,
        object[] columns,
        List<string> primaryKeys,
        Dictionary<string, object> parameters,
        ref int index)
    {
        Type columnType = typeof(QueryForm).GetNestedType("TableColumnInfo", BindingFlags.NonPublic);
        Type listType = typeof(List<>).MakeGenericType(columnType);
        System.Collections.IList typedColumns = (System.Collections.IList)Activator.CreateInstance(listType);
        foreach (object column in columns)
        {
            typedColumns.Add(column);
        }

        MethodInfo method = typeof(QueryForm).GetMethod("BuildWhereClause", BindingFlags.Instance | BindingFlags.NonPublic);
        object[] args = { row, typedColumns, primaryKeys, parameters, index };
        string where = (string)method.Invoke(form, args);
        index = (int)args[4];
        return where;
    }

    private static bool TryGetQueryFormStreamingExportSql(QueryForm form, QueryResultExportFormat format, out string sql)
    {
        MethodInfo method = typeof(QueryForm).GetMethod("TryGetStreamingExportSql", BindingFlags.Instance | BindingFlags.NonPublic);
        object[] args = { format, null };
        bool result = (bool)method.Invoke(form, args);
        sql = args[1] as string;
        return result;
    }

    private static void InvokeQueryFormExecuteUpdate(QueryForm form, string tableName, object[] columns, List<string> primaryKeys, DataRow row)
    {
        Type columnType = typeof(QueryForm).GetNestedType("TableColumnInfo", BindingFlags.NonPublic);
        Type listType = typeof(List<>).MakeGenericType(columnType);
        System.Collections.IList typedColumns = (System.Collections.IList)Activator.CreateInstance(listType);
        foreach (object column in columns)
        {
            typedColumns.Add(column);
        }

        MethodInfo method = typeof(QueryForm).GetMethod("ExecuteUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Invoke(form, new object[] { tableName, typedColumns, primaryKeys, row });
    }

    private static object CreateQueryFormColumnInfo(string name, bool isPrimaryKey)
    {
        Type columnType = typeof(QueryForm).GetNestedType("TableColumnInfo", BindingFlags.NonPublic);
        object column = Activator.CreateInstance(columnType, true);
        columnType.GetProperty("Name").SetValue(column, name, null);
        columnType.GetProperty("IsPrimaryKey").SetValue(column, isPrimaryKey, null);
        columnType.GetProperty("IsAutoIncrement").SetValue(column, false, null);
        return column;
    }

    private static void InvokeQueryFormExecOrThrow(QueryForm form, string sql, Dictionary<string, object> parameters, bool requireAffectedRow)
    {
        MethodInfo method = typeof(QueryForm).GetMethod("ExecOrThrow", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(Dictionary<string, object>), typeof(bool) }, null);
        method.Invoke(form, new object[] { sql, parameters, requireAffectedRow });
    }

    private static void TestDockableTabOptionService()
    {
        Assert(DockableTabOptionService.ResolveDockPreference(true, "main", false), "Main target should dock in the main tab area.");
        Assert(!DockableTabOptionService.ResolveDockPreference(true, "new", true), "New target should open as a floating window.");
        Assert(DockableTabOptionService.ResolveDockPreference(false, "last", true), "Last target should reuse docked tabs when tabs already exist.");
        Assert(!DockableTabOptionService.ResolveDockPreference(false, "last", false), "Last target should keep floating when no docked tab exists.");

        Assert(DockableTabOptionService.ShouldReuseTab(false, "main.users - table data", "main.users - table data", typeof(QueryForm), typeof(QueryForm)),
            "Duplicate disabled should reuse the same dockable title and type.");
        Assert(!DockableTabOptionService.ShouldReuseTab(true, "main.users", "main.users", typeof(QueryForm), typeof(QueryForm)),
            "Duplicate enabled should not reuse tabs.");
        Assert(!DockableTabOptionService.ShouldReuseTab(false, "main.users", "main.users", typeof(QueryForm), typeof(TableDesignerForm)),
            "Different dockable types should not be treated as duplicates.");
    }

    private static void TestDiagnosticLogService()
    {
        bool oldEnabled = ApplicationOptionSettings.GetBool("AdvancedEnableDiagnosticsLog");
        string oldLogDirectory = ApplicationOptionSettings.GetString("FileLogDirectory");
        string tempDirectory = Path.Combine(Path.GetTempPath(), "mysqlpunk_diag_" + Guid.NewGuid().ToString("N"));

        try
        {
            string sql = "SELECT secret_token, name FROM users WHERE id = 1";
            string line = DiagnosticLogService.BuildQueryHistoryJsonLine("main", sql, "OK", 42, 3, true);
            JObject entry = JObject.Parse(line);

            AssertEquals("query-history", entry.Value<string>("Category"), "Diagnostic query log should identify its category.");
            AssertEquals("main", entry.Value<string>("DatabaseName"), "Diagnostic query log should keep the database name.");
            Assert(entry.Value<string>("SqlSha256").Length == 64, "Diagnostic query log should include a SHA-256 fingerprint.");
            AssertContains(entry.Value<string>("SqlPreview"), "SELECT secret_token", "Diagnostic query log should include a short preview.");

            ApplicationOptionSettings.SetBool("AdvancedEnableDiagnosticsLog", true);
            ApplicationOptionSettings.SetString("FileLogDirectory", tempDirectory);
            DiagnosticLogService.AppendQueryHistory("main", sql, "OK", 42, 3, true);

            string[] files = Directory.GetFiles(tempDirectory, "diagnostics-*.jsonl");
            Assert(files.Length == 1, "Enabled diagnostics should write one JSONL log file.");
            string written = File.ReadAllText(files[0], Encoding.UTF8);
            AssertContains(written, "\"Category\":\"query-history\"", "Written diagnostic log should contain JSONL data.");

            File.Delete(files[0]);
            ApplicationOptionSettings.SetBool("AdvancedEnableDiagnosticsLog", false);
            DiagnosticLogService.AppendQueryHistory("main", sql, "OK", 42, 3, true);
            Assert(Directory.GetFiles(tempDirectory, "diagnostics-*.jsonl").Length == 0, "Disabled diagnostics should not write log files.");
        }
        finally
        {
            ApplicationOptionSettings.SetBool("AdvancedEnableDiagnosticsLog", oldEnabled);
            ApplicationOptionSettings.SetString("FileLogDirectory", oldLogDirectory);
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true);
        }
    }

    private static void TestAutoRecoveryDraftService()
    {
        bool oldEnabled = ApplicationOptionSettings.GetBool("AutoRecoveryQueryEnabled");
        int oldInterval = ApplicationOptionSettings.GetInt("AutoRecoveryIntervalSeconds");
        string oldQueryDirectory = ApplicationOptionSettings.GetString("FileQueryDirectory");
        string tempDirectory = Path.Combine(Path.GetTempPath(), "mysqlpunk_autorecovery_" + Guid.NewGuid().ToString("N"));

        try
        {
            ApplicationOptionSettings.SetBool("AutoRecoveryQueryEnabled", true);
            ApplicationOptionSettings.SetInt("AutoRecoveryIntervalSeconds", 2);
            ApplicationOptionSettings.SetString("FileQueryDirectory", tempDirectory);

            Assert(AutoRecoveryDraftService.GetQueryAutoRecoveryIntervalMilliseconds() == 5000, "Auto recovery interval should be clamped to 5 seconds.");

            string sql = "SELECT * FROM users WHERE id = 1";
            string json = AutoRecoveryDraftService.BuildQueryDraftJson("main", "host1", "Query - host1", sql);
            JObject draft = JObject.Parse(json);
            AssertEquals("main", draft.Value<string>("DatabaseName"), "Draft should include the database name.");
            AssertEquals(sql, draft.Value<string>("Sql"), "Draft should preserve SQL text.");
            Assert(draft.Value<string>("SqlSha256").Length == 64, "Draft should include a SQL fingerprint.");

            string path = AutoRecoveryDraftService.WriteQueryDraft("main", "host1", "Query - host1", sql);
            Assert(File.Exists(path), "Enabled auto recovery should write a draft file.");
            AssertContains(path, "auto-recovery", "Draft should be stored under the auto-recovery folder.");

            File.Delete(path);
            ApplicationOptionSettings.SetBool("AutoRecoveryQueryEnabled", false);
            string disabledPath = AutoRecoveryDraftService.WriteQueryDraft("main", "host1", "Query - host1", sql);
            AssertEquals(string.Empty, disabledPath, "Disabled auto recovery should skip draft writing.");
        }
        finally
        {
            ApplicationOptionSettings.SetBool("AutoRecoveryQueryEnabled", oldEnabled);
            ApplicationOptionSettings.SetInt("AutoRecoveryIntervalSeconds", oldInterval);
            ApplicationOptionSettings.SetString("FileQueryDirectory", oldQueryDirectory);
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true);
        }
    }

    private static void TestBinaryCellStreamingService()
    {
        byte[] payload = new byte[200000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        string tempPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_stream_" + Guid.NewGuid().ToString("N") + ".bin");
        my_sqlite db = new my_sqlite();
        try
        {
            string previousLanguage = Localization.CurrentLanguage;
            try
            {
                Localization.SetLanguage(Localization.TraditionalChinese, false);
                try
                {
                    BinaryCellStreamingService.WriteFirstColumnToFile(db, "", null, tempPath);
                    Assert(false, "Binary streaming export should require SQL text.");
                }
                catch (ArgumentException ex)
                {
                    AssertContains(ex.Message, "請輸入 SQL", "Binary streaming export should localize Traditional Chinese SQL validation errors.");
                }

                Localization.SetLanguage(Localization.English, false);
                try
                {
                    BinaryCellStreamingService.WriteFirstColumnToFile(db, "SELECT payload FROM stream_test;", null, "");
                    Assert(false, "Binary streaming export should require a target path.");
                }
                catch (ArgumentException ex)
                {
                    AssertContains(ex.Message, "Target path is required", "Binary streaming export should localize English target path errors.");
                }

                try
                {
                    BinaryCellStreamingService.WriteFirstColumnToFile(new FakeDumpDatabase(), "SELECT payload FROM stream_test;", null, tempPath);
                    Assert(false, "Binary streaming export should require a provider with a streaming connection.");
                }
                catch (NotSupportedException ex)
                {
                    AssertContains(ex.Message, "does not expose a streaming connection", "Binary streaming export should localize English unsupported provider errors.");
                }
            }
            finally
            {
                Localization.SetLanguage(previousLanguage, false);
            }

            db.SetConn("Data Source=:memory:;Version=3;New=True;");
            db.Open();
            Dictionary<string, string> createResult = db.ExecSQL("CREATE TABLE stream_test (id INTEGER PRIMARY KEY, payload BLOB);");
            AssertEquals("OK", createResult["status"], "SQLite test table should be created.");

            Dictionary<string, object> insertParameters = new Dictionary<string, object> { { "p0", 1 }, { "p1", payload } };
            Dictionary<string, string> insertResult = db.ExecSQL("INSERT INTO stream_test (id, payload) VALUES (@p0, @p1);", insertParameters);
            AssertEquals("OK", insertResult["status"], "SQLite test BLOB should be inserted.");

            long lastProgress = 0;
            long written = BinaryCellStreamingService.WriteFirstColumnToFile(
                db,
                "SELECT payload FROM stream_test WHERE id = @p0;",
                new Dictionary<string, object> { { "p0", 1 } },
                tempPath,
                (done, total) =>
                {
                    lastProgress = done;
                    Assert(total == payload.Length || total == -1, "Streaming progress should report total length or unknown length.");
                },
                4096);

            byte[] exported = File.ReadAllBytes(tempPath);
            Assert(written == payload.Length, "Streaming service should report exported byte count.");
            Assert(exported.Length == payload.Length, "Streaming service should export the full BLOB.");
            Assert(lastProgress == payload.Length, "Streaming progress should reach the final byte count.");
            for (int i = 0; i < payload.Length; i += 4097)
            {
                Assert(exported[i] == payload[i], "Streaming output byte should match source payload.");
            }
        }
        finally
        {
            db.Dispose();
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static void AssertViewDdlParseError(IDatabase database, string providerName, string expectedMessage)
    {
        try
        {
            database.CreateViewFromStatement("main", "broken_view", "CREATE VIEW broken_view");
            Assert(false, providerName + " should reject malformed View DDL before executing SQL.");
        }
        catch (Exception ex)
        {
            AssertContains(ex.Message, expectedMessage, providerName + " View DDL parse errors should localize.");
        }
    }

    private static void TestConnectionProfileService()
    {
        Type profileType = typeof(Form1).Assembly.GetType("mySQLPunk.entity.mySQLPunk_main");
        Assert(profileType != null, "Connection profile service type should exist.");
        object service = Activator.CreateInstance(profileType, true);
        MethodInfo copyProfileMethod = profileType.GetMethod("CopyProfile");
        MethodInfo renameProfileMethod = profileType.GetMethod("RenameProfile");
        MethodInfo deleteProfileMethod = profileType.GetMethod("DeleteProfile");
        Assert(copyProfileMethod != null && renameProfileMethod != null && deleteProfileMethod != null, "Connection profile service should expose profile operations.");

        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            try
            {
                copyProfileMethod.Invoke(service, new object[] { "default", "default" });
                Assert(false, "Copying over the default profile should fail.");
            }
            catch (TargetInvocationException ex)
            {
                InvalidOperationException operationException = ex.InnerException as InvalidOperationException;
                Assert(operationException != null, "Copying over the default profile should throw InvalidOperationException.");
                AssertContains(operationException.Message, "預設連線設定檔已存在", "Default profile copy errors should localize Traditional Chinese messages.");
            }

            try
            {
                renameProfileMethod.Invoke(service, new object[] { "default", "renamed" });
                Assert(false, "Renaming the default profile should fail.");
            }
            catch (TargetInvocationException ex)
            {
                InvalidOperationException operationException = ex.InnerException as InvalidOperationException;
                Assert(operationException != null, "Renaming the default profile should throw InvalidOperationException.");
                AssertContains(operationException.Message, "預設連線設定檔不可重新命名", "Default profile rename errors should localize Traditional Chinese messages.");
            }

            Localization.SetLanguage(Localization.English, false);
            try
            {
                deleteProfileMethod.Invoke(service, new object[] { "default" });
                Assert(false, "Deleting the default profile should fail.");
            }
            catch (TargetInvocationException ex)
            {
                InvalidOperationException operationException = ex.InnerException as InvalidOperationException;
                Assert(operationException != null, "Deleting the default profile should throw InvalidOperationException.");
                AssertContains(operationException.Message, "Default connection profile cannot be deleted", "Default profile delete errors should localize English messages.");
            }

            try
            {
                renameProfileMethod.Invoke(service, new object[] { "team", "default" });
                Assert(false, "Renaming a profile to the default profile name should fail.");
            }
            catch (TargetInvocationException ex)
            {
                InvalidOperationException operationException = ex.InnerException as InvalidOperationException;
                Assert(operationException != null, "Renaming to an existing profile should throw InvalidOperationException.");
                AssertContains(operationException.Message, "Connection profile already exists", "Profile exists errors should localize English messages.");
            }
        }
        finally
        {
            Localization.SetLanguage(previousLanguage, false);
        }
    }

    private static void TestConnectionAndMetadataServices()
    {
        FakeDumpDatabase openedDb = null;
        ConnectionOpenResult openResult = ConnectionOpenService.Open(() =>
        {
            openedDb = new FakeDumpDatabase();
            return openedDb;
        }, "Host=localhost;Database=main");

        Assert(ReferenceEquals(openedDb, openResult.Database), "ConnectionOpenService should return the opened database instance.");
        Assert(openedDb.WasOpened, "ConnectionOpenService should open the database.");
        AssertEquals("Host=localhost;Database=main", openedDb.ConnectionString, "ConnectionOpenService should set the connection string.");
        Assert(openResult.Databases.Count == 1 && openResult.Databases[0] == "main", "ConnectionOpenService should return database names.");
        Assert(ConnectionOpenService.ShouldOfferRetry(new TimeoutException("timeout")), "Timeout should be retryable.");
        Assert(!ConnectionOpenService.ShouldOfferRetry(new Exception("password authentication failed")), "Credential failures should not be retryable.");

        string connectionLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            MethodInfo metadataLoadFailedMethod = typeof(Form1).GetMethod("BuildDatabaseMetadataLoadFailedMessage", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(metadataLoadFailedMethod != null, "Database metadata load failure helper should be testable.");
            MethodInfo viewCopyPreviewMethod = typeof(Form1).GetMethod("BuildViewCopyPreview", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(viewCopyPreviewMethod != null, "View copy preview helper should be testable.");
            object copyPreviewForm = FormatterServices.GetUninitializedObject(typeof(Form1));
            string zhMetadataLoadFailed = (string)metadataLoadFailedMethod.Invoke(null, new object[] { "PostgreSQL", "sales", new InvalidOperationException("schema timeout") });
            AssertContains(zhMetadataLoadFailed, "PostgreSQL metadata 載入失敗（sales）", "Database metadata load failure should localize Traditional Chinese messages.");
            AssertContains(zhMetadataLoadFailed, "schema timeout", "Database metadata load failure should include provider errors.");
            AssertEquals("Metadata 載入", Localization.T("Metadata.Title"), "Database metadata error dialog title should localize Traditional Chinese messages.");

            try
            {
                MetadataLoadService blankErrorMetadataService = new MetadataLoadService(
                    (db, databaseName) => new DataTable(),
                    (db, databaseName, connInfo) => new DataTable(),
                    (db, databaseName) => new DataTable());
                blankErrorMetadataService.Load(new FakeDumpDatabase { ThrowOnGetTables = true, GetTablesExceptionMessage = "" }, "main", new Dictionary<string, object>());
                Assert(false, "Metadata loader should report table load failures.");
            }
            catch (Exception ex)
            {
                AssertContains(ex.Message, "載入 Tables 失敗：未知錯誤", "Metadata loader blank table errors should localize Traditional Chinese unknown errors.");
            }

            try
            {
                ConnectionOpenService.Open(() => null, "Host=localhost");
                Assert(false, "ConnectionOpenService should reject null database factory results.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "資料庫建立器未回傳連線物件", "ConnectionOpenService null factory errors should localize Traditional Chinese messages.");
            }

            DatabaseCopyService copyService = new DatabaseCopyService();
            DatabaseCopyItem connectedTableSource = new DatabaseCopyItem
            {
                Database = new FakeCopyDatabase("mysql"),
                DatabaseName = "main",
                ObjectName = "users",
                ObjectKind = "table",
                ProviderName = "mysql"
            };
            DatabaseCopyItem connectedTableTarget = new DatabaseCopyItem
            {
                Database = new FakeCopyDatabase("mysql"),
                DatabaseName = "main",
                ObjectName = "users",
                ObjectKind = "table",
                ProviderName = "mysql"
            };

            try
            {
                copyService.Copy(new DatabaseCopyItem { ObjectKind = "table" }, connectedTableTarget, null);
                Assert(false, "Database copy should require connected source and target databases.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "來源或目標資料庫尚未連線", "Database copy connection validation should localize Traditional Chinese messages.");
            }

            DatabaseCopyItem blankViewPreviewSource = new DatabaseCopyItem
            {
                Database = new FakeCopyDatabase("mysql") { ThrowOnViewCreateStatement = true, ViewCreateStatementExceptionMessage = "" },
                DatabaseName = "main",
                ObjectName = "active_users",
                ObjectKind = "view",
                ProviderName = "mysql"
            };
            ViewSqlConversionPreview zhBlankPreview = (ViewSqlConversionPreview)viewCopyPreviewMethod.Invoke(copyPreviewForm, new object[] { blankViewPreviewSource, connectedTableTarget });
            Assert(!zhBlankPreview.CanConvert, "View copy preview should mark blank DDL errors as not convertible.");
            AssertEquals("未知錯誤", zhBlankPreview.Reason, "Blank View copy preview errors should localize Traditional Chinese unknown fallback.");

            try
            {
                copyService.Copy(connectedTableSource, connectedTableTarget, null);
                Assert(false, "Database copy should reject source tables without copyable columns.");
            }
            catch (Exception ex)
            {
                AssertContains(ex.Message, "來源資料表沒有可複製的欄位", "Database copy empty column errors should localize Traditional Chinese messages.");
            }

            Localization.SetLanguage(Localization.English, false);
            string enMetadataLoadFailed = (string)metadataLoadFailedMethod.Invoke(null, new object[] { "SQL Server", "warehouse", new InvalidOperationException("permission denied") });
            AssertContains(enMetadataLoadFailed, "SQL Server metadata load failed (warehouse)", "Database metadata load failure should localize English messages.");
            AssertContains(enMetadataLoadFailed, "permission denied", "English database metadata load failure should include provider errors.");
            AssertEquals("Metadata Load", Localization.T("Metadata.Title"), "Database metadata error dialog title should localize English messages.");

            DatabaseCopyItem explicitViewPreviewSource = new DatabaseCopyItem
            {
                Database = new FakeCopyDatabase("postgresql") { ThrowOnViewCreateStatement = true, ViewCreateStatementExceptionMessage = " ddl unavailable " },
                DatabaseName = "main",
                ObjectName = "active_users",
                ObjectKind = "view",
                ProviderName = "postgresql"
            };
            ViewSqlConversionPreview enExplicitPreview = (ViewSqlConversionPreview)viewCopyPreviewMethod.Invoke(copyPreviewForm, new object[] { explicitViewPreviewSource, connectedTableTarget });
            Assert(!enExplicitPreview.CanConvert, "View copy preview should mark explicit DDL errors as not convertible.");
            AssertEquals("ddl unavailable", enExplicitPreview.Reason, "Explicit View copy preview errors should be trimmed and preserved.");

            DatabaseCopyItem unsupportedSource = new DatabaseCopyItem
            {
                Database = new FakeCopyDatabase("mysql", includeCopyColumns: true),
                DatabaseName = "main",
                ObjectName = "proc_ping",
                ObjectKind = "function",
                ProviderName = "mysql"
            };
            try
            {
                copyService.Copy(unsupportedSource, connectedTableTarget, null);
                Assert(false, "Database copy should reject unsupported object kinds.");
            }
            catch (NotSupportedException ex)
            {
                AssertContains(ex.Message, "Only Table / View copy is supported", "Database copy unsupported kind errors should localize English messages.");
            }

            DatabaseCopyItem emptyViewSource = new DatabaseCopyItem
            {
                Database = new FakeCopyDatabase("mysql", includeCopyColumns: true, viewSql: ""),
                DatabaseName = "main",
                ObjectName = "active_users",
                ObjectKind = "view",
                ProviderName = "mysql"
            };
            try
            {
                copyService.Copy(emptyViewSource, connectedTableTarget, null);
                Assert(false, "Database copy should reject views without DDL.");
            }
            catch (Exception ex)
            {
                AssertContains(ex.Message, "Cannot get View DDL", "Database copy missing view DDL errors should localize English messages.");
            }

            Localization.SetLanguage(Localization.TraditionalChinese, false);
            AssertViewDdlParseError(new my_mysql(), "MySQL", "無法解析 MySQL View DDL");
            AssertViewDdlParseError(new my_postgresql(), "PostgreSQL", "無法解析 PostgreSQL View DDL");
            AssertViewDdlParseError(new my_sqlite(), "SQLite", "無法解析 SQLite View DDL");

            Localization.SetLanguage(Localization.English, false);
            AssertViewDdlParseError(new my_mssql(), "SQL Server", "Cannot parse SQL Server View DDL");
            AssertViewDdlParseError(new my_oracle(), "Oracle", "Cannot parse Oracle View DDL");
            AssertContains(Localization.T("Object.ViewDdlUnavailable"), "Cannot get View DDL", "Empty View DDL provider errors should localize English messages.");
        }
        finally
        {
            Localization.SetLanguage(connectionLanguage, false);
        }

        MetadataLoadService metadataService = new MetadataLoadService(
            (db, name) => CreateNamedRowsTable("Name", "fn_ping"),
            (db, name, connInfo) => CreateNamedRowsTable("Name", connInfo["user"].ToString()),
            (db, name) => CreateNamedRowsTable("Name", "ev_daily"));
        DatabaseMetadataSnapshot snapshot = metadataService.Load(openedDb, "main", new Dictionary<string, object> { { "user", "tester" } });

        Assert(snapshot.Tables.Count == 1 && snapshot.Tables[0] == "public.users", "MetadataLoadService should load table names.");
        Assert(snapshot.Views.Count == 1 && snapshot.Views[0] == "public.active_users", "MetadataLoadService should load view names.");
        AssertEquals("fn_ping", snapshot.Functions.Rows[0]["Name"].ToString(), "MetadataLoadService should use the function loader.");
        AssertEquals("tester", snapshot.Users.Rows[0]["Name"].ToString(), "MetadataLoadService should pass connection info to the user loader.");
        AssertEquals("ev_daily", snapshot.Events.Rows[0]["Name"].ToString(), "MetadataLoadService should use the event loader.");

        FakeDumpDatabase sqliteMetadataDb = new FakeDumpDatabase
        {
            Provider = "sqlite",
            Tables = new List<string> { "users", "sqlite_sequence", "geometry_columns", "__mysqlpunk_column_comments", "idx_users_geometry_node" },
            Views = new List<string> { "active_users", "views_geometry_columns" }
        };
        DatabaseMetadataSnapshot visibleSnapshot = metadataService.Load(sqliteMetadataDb, "main", new Dictionary<string, object> { { "user", "tester" } }, false);
        Assert(visibleSnapshot.Tables.Count == 1 && visibleSnapshot.Tables[0] == "users", "MetadataLoadService should hide SQLite system tables by default.");
        Assert(visibleSnapshot.Views.Count == 1 && visibleSnapshot.Views[0] == "active_users", "MetadataLoadService should hide SQLite system views by default.");

        DatabaseMetadataSnapshot hiddenSnapshot = metadataService.Load(sqliteMetadataDb, "main", new Dictionary<string, object> { { "user", "tester" } }, true);
        Assert(hiddenSnapshot.Tables.Contains("sqlite_sequence") && hiddenSnapshot.Tables.Contains("__mysqlpunk_column_comments"), "MetadataLoadService should keep hidden objects when requested.");
        Assert(ObjectVisibilityService.FilterNames(new[] { "pg_class", "public.users" }, "postgresql", "table", false).SequenceEqual(new[] { "public.users" }), "Object visibility should hide PostgreSQL pg_* objects.");

        string metadataLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            try
            {
                FakeDumpDatabase throwingTablesDb = new FakeDumpDatabase { ThrowOnGetTables = true };
                metadataService.Load(throwingTablesDb, "main", new Dictionary<string, object>());
                Assert(false, "Metadata loader should wrap table load failures.");
            }
            catch (Exception ex)
            {
                AssertContains(ex.Message, "載入 Tables 失敗", "Metadata table load failures should localize Traditional Chinese messages.");
                AssertContains(ex.Message, "table timeout", "Metadata table load failure should keep the provider error message.");
            }

            Localization.SetLanguage(Localization.English, false);
            MetadataLoadService functionFailingMetadataService = new MetadataLoadService(
                (db, name) => { throw new InvalidOperationException("function denied"); },
                (db, name, connInfo) => CreateNamedRowsTable("Name", "tester"),
                (db, name) => CreateNamedRowsTable("Name", "ev_daily"));
            try
            {
                functionFailingMetadataService.Load(openedDb, "main", new Dictionary<string, object>());
                Assert(false, "Metadata loader should wrap function load failures.");
            }
            catch (Exception ex)
            {
                AssertContains(ex.Message, "Load Functions failed", "Metadata function load failures should localize English messages.");
                AssertContains(ex.Message, "function denied", "Metadata function load failure should keep the provider error message.");
            }
        }
        finally
        {
            Localization.SetLanguage(metadataLanguage, false);
        }

        object form = FormatterServices.GetUninitializedObject(typeof(Form1));
        MethodInfo schemaOverviewMethod = typeof(Form1).GetMethod("BuildSchemaOverviewModel", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo columnCatalogMethod = typeof(Form1).GetMethod("BuildColumnCatalogModel", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo indexCatalogMethod = typeof(Form1).GetMethod("BuildIndexCatalogModel", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo tableRowCountReportMethod = typeof(Form1).GetMethod("BuildTableRowCountReport", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo objectInventoryReportMethod = typeof(Form1).GetMethod("BuildObjectInventoryReport", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo objectDistributionBIMethod = typeof(Form1).GetMethod("BuildObjectDistributionBI", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo rowCountRankingBIMethod = typeof(Form1).GetMethod("BuildRowCountRankingBI", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo groupListMethod = typeof(Form1).GetMethod("BuildDatabaseGroupList", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo searchResultsMethod = typeof(Form1).GetMethod("BuildDatabaseSearchResults", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo detailObjectTypeMethod = typeof(Form1).GetMethod("LocalizeDetailObjectType", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo sidebarObjectTitleMethod = typeof(Form1).GetMethod("BuildSidebarObjectTitle", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo sidebarTitleMethod = typeof(Form1).GetMethod("BuildSidebarTitle", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo sidebarButtonTextMethod = typeof(Form1).GetMethod("BuildSidebarButtonText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo detailGridColumnHeaderMethod = typeof(Form1).GetMethod("BuildDetailGridColumnHeader", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo detailPropertyNameMethod = typeof(Form1).GetMethod("BuildDetailPropertyName", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo detailLoadErrorMethod = typeof(Form1).GetMethod("BuildDetailLoadErrorText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo detailNotFoundMethod = typeof(Form1).GetMethod("BuildDetailNotFoundText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo detailDdlHeaderLineMethod = typeof(Form1).GetMethod("BuildDetailDdlHeaderLine", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo detailDdlCommentLineMethod = typeof(Form1).GetMethod("BuildDetailDdlCommentLine", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo detailDdlDescriptionLineMethod = typeof(Form1).GetMethod("BuildDetailDdlDescriptionLine", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo favoriteStatusMethod = typeof(Form1).GetMethod("BuildFavoriteStatusText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo backupStatusMethod = typeof(Form1).GetMethod("BuildBackupStatusText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo unknownErrorMethod = typeof(Form1).GetMethod("BuildExecutionUnknownErrorText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo failureReasonMethod = typeof(Form1).GetMethod("BuildExecutionFailureReason", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo statusExceptionMessageMethod = typeof(Form1).GetMethod("BuildStatusExceptionMessage", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo formattedExceptionMessageMethod = typeof(Form1).GetMethod("BuildFormattedExceptionMessage", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo backupIntegrityExceptionStatusMethod = typeof(Form1).GetMethod("BuildBackupIntegrityExceptionStatusText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo diagnosticsMethod = typeof(Form1).GetMethod("BuildConnectionDiagnosticsTool", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo capabilitiesMethod = typeof(Form1).GetMethod("BuildProviderCapabilitiesTool", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo maintenanceMethod = typeof(Form1).GetMethod("BuildMaintenanceChecklistTool", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo connectionFailureMethod = typeof(Form1).GetMethod("BuildConnectionFailureMessage", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo connectionRetryPromptMethod = typeof(Form1).GetMethod("BuildConnectionRetryPromptMessage", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo spatiaLiteReadyStatusMethod = typeof(Form1).GetMethod("BuildSpatiaLiteReadyStatusText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo spatiaLiteRepairStatusMethod = typeof(Form1).GetMethod("BuildSpatiaLiteRepairStartedStatusText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo queryHistoryLoadedStatusMethod = typeof(Form1).GetMethod("BuildQueryHistoryLoadedStatusText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo queryTabOpenedStatusMethod = typeof(Form1).GetMethod("BuildQueryTabOpenedStatusText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo floatUndockMenuTextMethod = typeof(Form1).GetMethod("BuildFloatUndockMenuText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo databaseGroupMissingStatusMethod = typeof(Form1).GetMethod("BuildDatabaseGroupMissingStatusText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo modelDescriptionMethod = typeof(Form1).GetMethod("GetDatabaseModelDescription", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo biDescriptionMethod = typeof(Form1).GetMethod("GetDatabaseBIDescription", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo otherDescriptionMethod = typeof(Form1).GetMethod("GetDatabaseOtherToolDescription", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo reportDescriptionMethod = typeof(Form1).GetMethod("GetDatabaseReportDescription", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo functionTemplateMethod = typeof(Form1).GetMethod("BuildFunctionTemplate", BindingFlags.Static | BindingFlags.NonPublic);
        string oldLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            AssertEquals("MySQL 連線失敗：timeout", (string)connectionFailureMethod.Invoke(null, new object[] { "MySQL", new TimeoutException("timeout") }), "Connection failure messages should localize Traditional Chinese.");
            AssertContains((string)connectionRetryPromptMethod.Invoke(null, new object[] { "PostgreSQL", new TimeoutException("timeout") }), "是否要重試一次？", "Connection retry prompts should localize Traditional Chinese.");
            AssertEquals("SQLite + SpatiaLite 就緒", (string)spatiaLiteReadyStatusMethod.Invoke(null, new object[0]), "SpatiaLite ready status should localize Traditional Chinese.");
            AssertEquals("SpatiaLite runtime 修復腳本已啟動。", (string)spatiaLiteRepairStatusMethod.Invoke(null, new object[0]), "SpatiaLite repair status should localize Traditional Chinese.");
            AssertEquals("查詢歷程已載入：main", (string)queryHistoryLoadedStatusMethod.Invoke(null, new object[] { "main" }), "Query history status should localize Traditional Chinese.");
            AssertEquals("查詢分頁已開啟：Query 1", (string)queryTabOpenedStatusMethod.Invoke(null, new object[] { "Query 1" }), "Query tab status should localize Traditional Chinese.");
            AssertEquals("浮動 / 取消停靠", (string)floatUndockMenuTextMethod.Invoke(null, new object[0]), "Query tab float/undock menu should localize Traditional Chinese.");
            AssertEquals("目前資料庫沒有 Reports 節點。", (string)databaseGroupMissingStatusMethod.Invoke(null, new object[] { "Reports" }), "Missing database group status should localize Traditional Chinese.");
            AssertContains((string)modelDescriptionMethod.Invoke(null, new object[] { "Column Catalog" }), "欄位", "Model descriptions should localize Traditional Chinese text.");
            AssertContains((string)biDescriptionMethod.Invoke(null, new object[] { "Table Size Summary" }), "資料長度", "BI descriptions should localize Traditional Chinese text.");
            AssertContains((string)otherDescriptionMethod.Invoke(null, new object[] { "Provider Capabilities" }), "可用功能", "Other tool descriptions should localize Traditional Chinese text.");
            AssertContains((string)reportDescriptionMethod.Invoke(null, new object[] { "Object Inventory" }), "備份目標", "Report descriptions should localize Traditional Chinese text.");

            DataTable zhSchemaOverview = (DataTable)schemaOverviewMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            DataRow zhSchemaTable = FindDataRow(zhSchemaOverview, "名稱", "public.users");
            Assert(zhSchemaTable != null, "Schema overview should include fake table metadata.");
            AssertContains(zhSchemaTable["類型"].ToString(), "資料表", "Schema overview should localize Traditional Chinese table type.");
            AssertContains(zhSchemaTable["狀態"].ToString(), "就緒", "Schema overview should localize Traditional Chinese ready status.");

            DataTable zhColumnCatalog = (DataTable)columnCatalogMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            DataRow zhColumn = FindDataRow(zhColumnCatalog, "物件", "public.users");
            Assert(zhColumn != null, "Column catalog should include fake table column metadata.");
            AssertContains(zhColumn["類型"].ToString(), "資料表", "Column catalog should localize Traditional Chinese table type.");

            DataTable zhIndexCatalog = (DataTable)indexCatalogMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            DataRow zhNoIndex = FindDataRow(zhIndexCatalog, "資料表", "public.users");
            Assert(zhNoIndex != null, "Index catalog should include fake table metadata.");
            AssertContains(zhNoIndex["索引"].ToString(), "沒有明確索引", "Index catalog should localize Traditional Chinese empty-index fallback.");

            DataTable zhRowCounts = (DataTable)tableRowCountReportMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            DataRow zhRowCount = FindDataRow(zhRowCounts, "資料表", "public.users");
            Assert(zhRowCount != null, "Table row count report should include fake table metadata.");
            AssertContains(zhRowCount["狀態"].ToString(), "就緒", "Table row count report should localize Traditional Chinese ready status.");

            DataTable zhFailedRowCounts = (DataTable)tableRowCountReportMethod.Invoke(form, new object[] { new FakeDumpDatabase { ThrowOnCountRows = true, CountRowsExceptionMessage = "" }, "main" });
            DataRow zhFailedRowCount = FindDataRow(zhFailedRowCounts, "資料表", "public.users");
            Assert(zhFailedRowCount != null, "Table row count report should keep rows when count fails.");
            AssertEquals("未知錯誤", zhFailedRowCount["狀態"].ToString(), "Blank table row count errors should localize Traditional Chinese unknown fallback.");

            DataTable zhInventory = (DataTable)objectInventoryReportMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", new Dictionary<string, object>() });
            DataRow zhInventoryTable = FindDataRow(zhInventory, "名稱", "public.users");
            Assert(zhInventoryTable != null, "Object inventory should include fake table metadata.");
            AssertContains(zhInventoryTable["類型"].ToString(), "資料表", "Object inventory should localize Traditional Chinese table type.");
            AssertContains(zhInventoryTable["狀態"].ToString(), "就緒", "Object inventory should localize Traditional Chinese ready status.");

            DataTable zhDistribution = (DataTable)objectDistributionBIMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            Assert(FindDataRow(zhDistribution, "類別", "資料表") != null, "Object distribution BI should localize Traditional Chinese table category.");

            DataTable zhRanking = (DataTable)rowCountRankingBIMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            DataRow zhRankingTable = FindDataRow(zhRanking, "名稱", "public.users");
            Assert(zhRankingTable != null, "Row count ranking BI should include fake table metadata.");
            AssertContains(zhRankingTable["類型"].ToString(), "資料表", "Row count ranking BI should localize Traditional Chinese table type.");
            AssertContains(zhRankingTable["狀態"].ToString(), "就緒", "Row count ranking BI should localize Traditional Chinese ready status.");

            DataTable zhFailedRanking = (DataTable)rowCountRankingBIMethod.Invoke(form, new object[] { new FakeDumpDatabase { ThrowOnCountRows = true, CountRowsExceptionMessage = " " }, "main" });
            DataRow zhFailedRankingTable = FindDataRow(zhFailedRanking, "名稱", "public.users");
            Assert(zhFailedRankingTable != null, "Row count ranking BI should keep rows when count fails.");
            AssertEquals("未知錯誤", zhFailedRankingTable["狀態"].ToString(), "Blank ranking row count errors should localize Traditional Chinese unknown fallback.");

            DataTable zhViewsGroup = (DataTable)groupListMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", "Views", new Dictionary<string, object>() });
            DataRow zhViewGroupRow = FindDataRow(zhViewsGroup, "名稱", "public.active_users");
            Assert(zhViewGroupRow != null, "Database views group should include fake view metadata.");
            AssertContains(zhViewGroupRow["類型"].ToString(), "檢視", "Database views group should localize Traditional Chinese view type.");
            AssertContains(zhViewGroupRow["狀態"].ToString(), "就緒", "Database views group should localize Traditional Chinese ready status.");

            DataTable zhModelsGroup = (DataTable)groupListMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", "Models", new Dictionary<string, object>() });
            DataRow zhModelRow = FindDataRow(zhModelsGroup, "名稱", "Schema Overview");
            Assert(zhModelRow != null, "Database models group should include schema overview.");
            AssertContains(zhModelRow["類型"].ToString(), "模型", "Database models group should localize Traditional Chinese model type.");
            AssertContains(zhModelRow["描述"].ToString(), "欄位數", "Database models group should localize Traditional Chinese descriptions.");

            DataTable zhUnknownGroup = (DataTable)groupListMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", "Unknown", new Dictionary<string, object>() });
            DataRow zhUnknownRow = FindDataRow(zhUnknownGroup, "名稱", "Unknown");
            Assert(zhUnknownRow != null, "Unknown database group should include a fallback row.");
            AssertContains(zhUnknownRow["狀態"].ToString(), "空白", "Unknown database group should localize Traditional Chinese empty status.");

            DataTable zhSearch = (DataTable)searchResultsMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", "users" });
            DataRow zhSearchTable = FindDataRow(zhSearch, "名稱", "public.users");
            Assert(zhSearchTable != null, "Database search should include matching fake table metadata.");
            AssertContains(zhSearchTable["類型"].ToString(), "資料表", "Database search should localize Traditional Chinese table type.");
            AssertContains(zhSearchTable["位置"].ToString(), "資料表", "Database search should localize Traditional Chinese table location.");
            DataRow zhSearchColumn = FindDataRow(zhSearch, "欄位", "id");
            Assert(zhSearchColumn != null, "Database search should include matching fake column metadata.");
            AssertContains(zhSearchColumn["類型"].ToString(), "欄位", "Database search should localize Traditional Chinese column type.");

            AssertContains((string)detailObjectTypeMethod.Invoke(null, new object[] { "View" }), "檢視", "Detail panel should localize Traditional Chinese view type.");
            AssertContains((string)detailObjectTypeMethod.Invoke(null, new object[] { "Function" }), "函式", "Detail panel should localize Traditional Chinese function type.");
            AssertContains((string)detailObjectTypeMethod.Invoke(null, new object[] { "User" }), "使用者", "Detail panel should localize Traditional Chinese user type.");
            AssertContains((string)detailObjectTypeMethod.Invoke(null, new object[] { "Model" }), "模型", "Detail panel should localize Traditional Chinese model type.");
            AssertContains((string)detailObjectTypeMethod.Invoke(null, new object[] { "Other" }), "其它", "Detail panel should localize Traditional Chinese other type.");
            AssertContains((string)detailObjectTypeMethod.Invoke(null, new object[] { "Report" }), "報表", "Detail panel should localize Traditional Chinese report type.");
            AssertEquals("資料庫：main", (string)sidebarObjectTitleMethod.Invoke(null, new object[] { "Database", "main" }), "Sidebar title should localize Traditional Chinese database title.");
            AssertEquals("檢視：public.active_users", (string)sidebarObjectTitleMethod.Invoke(null, new object[] { "View", "public.active_users" }), "Sidebar title should localize Traditional Chinese view title.");
            AssertEquals("在資料庫中尋找：main", (string)sidebarTitleMethod.Invoke(null, new object[] { Localization.T("Database.SearchTitle"), "main" }), "Sidebar title should localize Traditional Chinese custom labels.");
            AssertEquals("資訊", (string)sidebarButtonTextMethod.Invoke(null, new object[] { "Info" }), "Sidebar Info button should localize Traditional Chinese.");
            AssertEquals("DDL", (string)sidebarButtonTextMethod.Invoke(null, new object[] { "DDL" }), "Sidebar DDL button should keep the standard label in Traditional Chinese.");
            AssertEquals("屬性", (string)detailGridColumnHeaderMethod.Invoke(null, new object[] { "Key" }), "Detail grid property header should localize Traditional Chinese.");
            AssertEquals("值", (string)detailGridColumnHeaderMethod.Invoke(null, new object[] { "Value" }), "Detail grid value header should localize Traditional Chinese.");
            AssertEquals("字元集", (string)detailPropertyNameMethod.Invoke(null, new object[] { "CharacterSet" }), "Detail property names should localize Traditional Chinese character set.");
            AssertEquals("回傳型別", (string)detailPropertyNameMethod.Invoke(null, new object[] { "ReturnType" }), "Detail property names should localize Traditional Chinese return type.");
            AssertEquals("資料可用空間", (string)detailPropertyNameMethod.Invoke(null, new object[] { "DataFree" }), "Detail property names should localize Traditional Chinese free space.");
            AssertContains((string)detailLoadErrorMethod.Invoke(null, new object[] { "View", "boom" }), "載入檢視細節失敗", "Detail panel should localize Traditional Chinese view load errors.");
            AssertContains((string)detailLoadErrorMethod.Invoke(null, new object[] { "Function", "boom" }), "載入函式細節失敗", "Detail panel should localize Traditional Chinese function load errors.");
            AssertContains((string)detailNotFoundMethod.Invoke(null, new object[] { "Event", "nightly" }), "找不到事件", "Detail panel should localize Traditional Chinese event not-found message.");
            AssertContains((string)detailNotFoundMethod.Invoke(null, new object[] { "User", "alice" }), "找不到使用者", "Detail panel should localize Traditional Chinese user not-found message.");
            AssertEquals("-- 模型：Schema Overview", (string)detailDdlHeaderLineMethod.Invoke(null, new object[] { "Model", "Schema Overview" }), "Detail DDL header should localize Traditional Chinese object type and separator.");
            AssertEquals("-- 來源：metadata", (string)detailDdlCommentLineMethod.Invoke(null, new object[] { "Source", "metadata" }), "Detail DDL comment labels should localize Traditional Chinese properties.");
            AssertEquals("-- 彙整資料表與檢視的欄位數、索引數與列數", (string)detailDdlDescriptionLineMethod.Invoke(null, new object[] { (string)modelDescriptionMethod.Invoke(null, new object[] { "Schema Overview" }) }), "Detail DDL description comments should preserve localized Traditional Chinese descriptions.");
            AssertEquals("已加入我的最愛：Root\\main", (string)favoriteStatusMethod.Invoke(null, new object[] { "Added", "Root\\main" }), "Favorites should localize Traditional Chinese added status.");
            AssertEquals("已移出我的最愛：Root\\main", (string)favoriteStatusMethod.Invoke(null, new object[] { "Removed", "Root\\main" }), "Favorites should localize Traditional Chinese removed status.");
            AssertEquals("找不到我的最愛：Missing\\Node", (string)favoriteStatusMethod.Invoke(null, new object[] { "NotFound", "Missing\\Node" }), "Favorites should localize Traditional Chinese not-found status.");
            AssertEquals("已清除我的最愛。", (string)favoriteStatusMethod.Invoke(null, new object[] { "Cleared", "" }), "Favorites should localize Traditional Chinese cleared status.");
            AssertEquals("備份已建立：C:\\backup.sql", (string)backupStatusMethod.Invoke(null, new object[] { "Created", "C:\\backup.sql" }), "Backup status should localize Traditional Chinese created status.");
            AssertEquals("建立備份失敗：disk full", (string)backupStatusMethod.Invoke(null, new object[] { "Failed", "disk full" }), "Backup status should localize Traditional Chinese failed status.");
            AssertEquals("建立備份失敗：未知錯誤", (string)backupStatusMethod.Invoke(null, new object[] { "Failed", "   " }), "Blank backup failures should localize Traditional Chinese unknown errors.");
            AssertEquals("未知錯誤", (string)unknownErrorMethod.Invoke(null, new object[0]), "Execution fallback should localize Traditional Chinese unknown errors.");
            AssertEquals("未知錯誤", (string)failureReasonMethod.Invoke(null, new object[] { new Dictionary<string, string> { { "reason", "   " } } }), "Blank execution reasons should fall back to Traditional Chinese unknown errors.");
            AssertEquals("provider denied", (string)failureReasonMethod.Invoke(null, new object[] { new Dictionary<string, string> { { "reason", "provider denied" } } }), "Execution failure reasons should preserve provider messages.");
            AssertEquals("匯出失敗：未知錯誤", (string)statusExceptionMessageMethod.Invoke(null, new object[] { "Status.ExportFailed", new Exception("") }), "Blank export exceptions should localize Traditional Chinese unknown errors.");
            AssertEquals("匯入 SQL 失敗：語法錯誤", (string)statusExceptionMessageMethod.Invoke(null, new object[] { "ImportSql.Failed", new InvalidOperationException(" 語法錯誤 ") }), "SQL import exception messages should preserve explicit Traditional Chinese reasons.");
            AssertEquals("匯出 SQL 失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Object.SqlExportFailed", new Exception("") }), "Blank SQL export exceptions should localize Traditional Chinese unknown errors.");
            AssertEquals("重新命名失敗：name exists", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Object.RenameFailed", new InvalidOperationException(" name exists ") }), "Formatted exception messages should preserve explicit Traditional Chinese reasons.");
            AssertEquals("複製失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Object.CopyFailed", new Exception("   ") }), "Blank copy exceptions should localize Traditional Chinese unknown errors.");
            AssertEquals("載入資料表清單失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Database.DataGenerationLoadFailed", new Exception("") }), "Blank data generation load errors should localize Traditional Chinese unknown errors.");
            AssertEquals("無法載入自動註解字典：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Designer.AutoCommentsLoadFailed", new Exception("") }), "Blank auto comment load errors should localize Traditional Chinese unknown errors.");
            AssertEquals("補註解失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Database.AutoCommentsFailed", new Exception("") }), "Blank database auto comment errors should localize Traditional Chinese unknown errors.");
            AssertEquals("檢查更新失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Update.CheckFailed", new Exception("") }), "Blank update check errors should localize Traditional Chinese unknown errors.");
            AssertEquals("加入信任來源失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Connection.ImportTrustSourceFailed", new Exception("") }), "Blank trusted-source errors should localize Traditional Chinese unknown errors.");
            AssertEquals("匯出 SQLite 欄位註解失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Object.SqliteColumnCommentsExportFailed", new Exception("") }), "Blank SQLite column comment export errors should localize Traditional Chinese unknown errors.");
            AssertEquals("匯入 SQLite 欄位註解失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Object.SqliteColumnCommentsImportFailed", new Exception("   ") }), "Blank SQLite column comment import errors should localize Traditional Chinese unknown errors.");
            AssertEquals("還原備份失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Backup.RestoreFailed", new Exception("") }), "Blank restore errors should localize Traditional Chinese unknown errors.");
            AssertEquals("還原隔離備份失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Backup.QuarantineRestoreFailed", new Exception("") }), "Blank quarantine restore errors should localize Traditional Chinese unknown errors.");
            AssertEquals("還原內容掃描報表建立失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Backup.RestoreContentScanReportFailed", new Exception("") }), "Blank restore content scan report errors should localize Traditional Chinese unknown errors.");
            AssertEquals("建立還原前快照失敗，已取消還原：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Backup.RestoreSafetyBackupFailedCancelled", new Exception("") }), "Blank pre-restore snapshot errors should localize Traditional Chinese unknown errors.");
            AssertEquals("資料寫入失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Database.DataGenerationExecuteFailed", new Exception("") }), "Blank generated data execution errors should localize Traditional Chinese unknown errors.");
            AssertEquals("刪除資料庫失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Database.DeleteFailed", new Exception("") }), "Blank database delete errors should localize Traditional Chinese unknown errors.");
            AssertEquals("刪除前備份失敗，已取消刪除：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Database.PreDeleteBackupFailedCancelled", new Exception("") }), "Blank pre-delete backup errors should localize Traditional Chinese unknown errors.");
            AssertEquals("新增資料庫失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Database.CreateFailed", new Exception("   ") }), "Blank database create errors should localize Traditional Chinese unknown errors.");
            AssertEquals("分享連線失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Connection.ShareFailed", new Exception("") }), "Blank share connection errors should localize Traditional Chinese unknown errors.");
            AssertEquals("開啟命令列介面失敗：未知錯誤", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Connection.CommandLineOpenFailed", new Exception("") }), "Blank command line open errors should localize Traditional Chinese unknown errors.");
            AssertEquals("備份完整性定期驗證發現 1 個異常檔案。 未知錯誤", (string)backupIntegrityExceptionStatusMethod.Invoke(null, new object[] { new Exception("") }), "Blank backup integrity exceptions should localize Traditional Chinese unknown errors.");
            string zhSqliteFunctionTemplate = (string)functionTemplateMethod.Invoke(null, new object[] { new my_sqlite(), "main", "" });
            AssertContains(zhSqliteFunctionTemplate, "SQLite 不會把函式儲存在資料庫 schema", "SQLite function template should localize Traditional Chinese schema limitation comments.");
            AssertContains(zhSqliteFunctionTemplate, "應用程式自訂 SQLite 函式必須由用戶端連線註冊", "SQLite function template should localize Traditional Chinese client-defined comments.");
            AssertContains(zhSqliteFunctionTemplate, "SELECT 1;", "SQLite function template should keep executable fallback SQL.");

            SeedQueryHistory(form);
            MethodInfo queryHistoryMethod = typeof(Form1).GetMethod("BuildQueryHistoryTable", BindingFlags.Instance | BindingFlags.NonPublic);
            DataTable zhQueryHistory = (DataTable)queryHistoryMethod.Invoke(form, new object[] { "main" });
            DataRow zhQueryHistoryQuery = FindDataRow(zhQueryHistory, "SQL", "SELECT 1");
            Assert(zhQueryHistoryQuery != null, "Query history should include seeded query entry.");
            AssertContains(zhQueryHistoryQuery["類型"].ToString(), "查詢", "Query history should localize Traditional Chinese query type.");
            DataRow zhQueryHistoryCommand = FindDataRow(zhQueryHistory, "SQL", "UPDATE users SET name = 'A'");
            Assert(zhQueryHistoryCommand != null, "Query history should include seeded command entry.");
            AssertContains(zhQueryHistoryCommand["類型"].ToString(), "命令", "Query history should localize Traditional Chinese command type.");

            DataTable zhDiagnostics = (DataTable)diagnosticsMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", new Dictionary<string, object>() });
            DataRow zhConnectionState = FindDataRow(zhDiagnostics, "項目", "連線狀態");
            Assert(zhConnectionState != null, "Connection diagnostics should localize Traditional Chinese connection state item.");
            AssertContains(zhConnectionState["狀態"].ToString(), "就緒", "Connection diagnostics should localize ready status.");
            DataRow zhDatabase = FindDataRow(zhDiagnostics, "項目", "資料庫");
            Assert(zhDatabase != null, "Connection diagnostics should localize Traditional Chinese database item.");
            AssertEquals("main", zhDatabase["說明"].ToString(), "Connection diagnostics should keep the database name as detail.");

            using (my_sqlite sqlite = new my_sqlite())
            {
                DataTable sqliteCapabilities = (DataTable)capabilitiesMethod.Invoke(form, new object[] { sqlite, "main" });
                DataRow storedFunctions = FindDataRow(sqliteCapabilities, "項目", "Stored Functions");
                Assert(storedFunctions != null, "Provider capabilities should include stored functions.");
                AssertContains(storedFunctions["狀態"].ToString(), "不支援", "SQLite stored functions capability should localize unavailable status.");
                AssertContains(storedFunctions["說明"].ToString(), "SQLite", "SQLite stored functions capability should explain the provider limitation.");
            }

            DataTable zhMaintenance = (DataTable)maintenanceMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", new Dictionary<string, object>() });
            DataRow zhTablesLoaded = FindDataRow(zhMaintenance, "項目", "已載入資料表");
            Assert(zhTablesLoaded != null, "Maintenance checklist should localize Traditional Chinese table item.");
            AssertContains(zhTablesLoaded["狀態"].ToString(), "正常", "Maintenance checklist should localize OK status.");
            AssertContains(zhTablesLoaded["說明"].ToString(), "資料表", "Maintenance checklist should localize table count detail.");
            DataRow zhOpenTabs = FindDataRow(zhMaintenance, "項目", "開啟中的查詢分頁");
            Assert(zhOpenTabs != null, "Maintenance checklist should localize Traditional Chinese query tabs item.");
            AssertContains(zhOpenTabs["說明"].ToString(), "分頁", "Maintenance checklist should localize query tab count detail.");

            Localization.SetLanguage(Localization.English, false);
            AssertEquals("MySQL connection failed: timeout", (string)connectionFailureMethod.Invoke(null, new object[] { "MySQL", new TimeoutException("timeout") }), "Connection failure messages should support English.");
            AssertContains((string)connectionRetryPromptMethod.Invoke(null, new object[] { "PostgreSQL", new TimeoutException("timeout") }), "Retry once?", "Connection retry prompts should support English.");
            AssertEquals("SQLite + SpatiaLite ready", (string)spatiaLiteReadyStatusMethod.Invoke(null, new object[0]), "SpatiaLite ready status should support English.");
            AssertEquals("SpatiaLite runtime repair script started.", (string)spatiaLiteRepairStatusMethod.Invoke(null, new object[0]), "SpatiaLite repair status should support English.");
            AssertEquals("Query history loaded: main", (string)queryHistoryLoadedStatusMethod.Invoke(null, new object[] { "main" }), "Query history status should support English.");
            AssertEquals("Query tab opened: Query 1", (string)queryTabOpenedStatusMethod.Invoke(null, new object[] { "Query 1" }), "Query tab status should support English.");
            AssertEquals("Float / Undock", (string)floatUndockMenuTextMethod.Invoke(null, new object[0]), "Query tab float/undock menu should support English.");
            AssertEquals("Current database has no Reports node.", (string)databaseGroupMissingStatusMethod.Invoke(null, new object[] { "Reports" }), "Missing database group status should support English.");
            AssertContains((string)modelDescriptionMethod.Invoke(null, new object[] { "Column Catalog" }), "columns", "Model descriptions should support English text.");
            AssertContains((string)biDescriptionMethod.Invoke(null, new object[] { "Table Size Summary" }), "data length", "BI descriptions should support English text.");
            AssertContains((string)otherDescriptionMethod.Invoke(null, new object[] { "Provider Capabilities" }), "capabilities", "Other tool descriptions should support English text.");
            AssertContains((string)reportDescriptionMethod.Invoke(null, new object[] { "Object Inventory" }), "backup targets", "Report descriptions should support English text.");
            string enSqliteFunctionTemplate = (string)functionTemplateMethod.Invoke(null, new object[] { new my_sqlite(), "main", "" });
            AssertContains(enSqliteFunctionTemplate, "SQLite does not store functions in the database schema", "SQLite function template should support English schema limitation comments.");
            AssertContains(enSqliteFunctionTemplate, "Application-defined SQLite functions must be registered by the client connection", "SQLite function template should support English client-defined comments.");

            DataTable postgresCapabilities = (DataTable)capabilitiesMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            DataRow tablesCapability = FindDataRow(postgresCapabilities, "項目", "Tables");
            Assert(tablesCapability != null, "Provider capabilities should include tables.");
            AssertContains(tablesCapability["狀態"].ToString(), "Supported", "Provider capabilities should support English status labels.");
            AssertContains(tablesCapability["說明"].ToString(), "loaded", "Provider capabilities should support English detail labels.");

            DataTable enDiagnostics = (DataTable)diagnosticsMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", new Dictionary<string, object>() });
            DataRow enProvider = FindDataRow(enDiagnostics, "項目", "Provider");
            Assert(enProvider != null, "Connection diagnostics should support English provider item.");
            AssertContains(enProvider["狀態"].ToString(), "Ready", "Connection diagnostics should support English ready status.");

            DataTable enSchemaOverview = (DataTable)schemaOverviewMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            DataRow enSchemaView = FindDataRow(enSchemaOverview, "名稱", "public.active_users");
            Assert(enSchemaView != null, "Schema overview should include fake view metadata.");
            AssertEquals("View", enSchemaView["類型"].ToString(), "Schema overview should support English view type.");
            AssertEquals("Ready", enSchemaView["狀態"].ToString(), "Schema overview should support English ready status.");

            DataTable enColumnCatalog = (DataTable)columnCatalogMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            DataRow enColumn = FindDataRow(enColumnCatalog, "物件", "public.active_users");
            Assert(enColumn != null, "Column catalog should include fake view column metadata.");
            AssertEquals("View", enColumn["類型"].ToString(), "Column catalog should support English view type.");

            DataTable enIndexCatalog = (DataTable)indexCatalogMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            DataRow enNoIndex = FindDataRow(enIndexCatalog, "資料表", "public.users");
            Assert(enNoIndex != null, "Index catalog should include fake table metadata.");
            AssertEquals("(no explicit indexes)", enNoIndex["索引"].ToString(), "Index catalog should support English empty-index fallback.");

            DataTable enInventory = (DataTable)objectInventoryReportMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", new Dictionary<string, object>() });
            DataRow enInventoryView = FindDataRow(enInventory, "名稱", "public.active_users");
            Assert(enInventoryView != null, "Object inventory should include fake view metadata.");
            AssertEquals("View", enInventoryView["類型"].ToString(), "Object inventory should support English view type.");
            AssertEquals("Ready", enInventoryView["狀態"].ToString(), "Object inventory should support English ready status.");

            DataTable enDistribution = (DataTable)objectDistributionBIMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            Assert(FindDataRow(enDistribution, "類別", "Tables") != null, "Object distribution BI should support English table category.");

            DataTable enRanking = (DataTable)rowCountRankingBIMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main" });
            DataRow enRankingView = FindDataRow(enRanking, "名稱", "public.active_users");
            Assert(enRankingView != null, "Row count ranking BI should include fake view metadata.");
            AssertEquals("View", enRankingView["類型"].ToString(), "Row count ranking BI should support English view type.");
            AssertEquals("Ready", enRankingView["狀態"].ToString(), "Row count ranking BI should support English ready status.");

            DataTable enFailedRowCounts = (DataTable)tableRowCountReportMethod.Invoke(form, new object[] { new FakeDumpDatabase { ThrowOnCountRows = true, CountRowsExceptionMessage = "" }, "main" });
            DataRow enFailedRowCount = FindDataRow(enFailedRowCounts, "資料表", "public.users");
            Assert(enFailedRowCount != null, "Table row count report should keep rows when count fails in English.");
            AssertEquals("Unknown error", enFailedRowCount["狀態"].ToString(), "Blank table row count errors should localize English unknown fallback.");

            DataTable enFailedRanking = (DataTable)rowCountRankingBIMethod.Invoke(form, new object[] { new FakeDumpDatabase { ThrowOnCountRows = true, CountRowsExceptionMessage = " row count timeout " }, "main" });
            DataRow enFailedRankingTable = FindDataRow(enFailedRanking, "名稱", "public.users");
            Assert(enFailedRankingTable != null, "Row count ranking BI should keep table rows when count fails in English.");
            AssertEquals("row count timeout", enFailedRankingTable["狀態"].ToString(), "Explicit ranking row count errors should be trimmed and preserved.");

            DataTable enReportsGroup = (DataTable)groupListMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", "Reports", new Dictionary<string, object>() });
            DataRow enReportRow = FindDataRow(enReportsGroup, "名稱", "Database Summary");
            Assert(enReportRow != null, "Database reports group should include database summary.");
            AssertEquals("Report", enReportRow["類型"].ToString(), "Database reports group should support English report type.");
            AssertEquals("Ready", enReportRow["狀態"].ToString(), "Database reports group should support English ready status.");
            AssertContains(enReportRow["描述"].ToString(), "object counts", "Database reports group should support English descriptions.");

            DataTable enBackupsGroup = (DataTable)groupListMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", "Backups", new Dictionary<string, object>() });
            DataRow enBackupRow = FindDataRow(enBackupsGroup, "名稱", "main");
            Assert(enBackupRow != null, "Database backups group should include a backup row.");
            AssertEquals("SQL Dump", enBackupRow["類型"].ToString(), "Database backups group should support English SQL dump type.");
            AssertEquals("Ready", enBackupRow["狀態"].ToString(), "Database backups group should support English ready status.");
            AssertEquals("(logical backup)", enBackupRow["路徑"].ToString(), "Database backups group should support English logical backup path.");

            DataTable enSearch = (DataTable)searchResultsMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", "active" });
            DataRow enSearchView = FindDataRow(enSearch, "名稱", "public.active_users");
            Assert(enSearchView != null, "Database search should include matching fake view metadata.");
            AssertEquals("View", enSearchView["類型"].ToString(), "Database search should support English view type.");
            AssertEquals("Views", enSearchView["位置"].ToString(), "Database search should support English view location.");

            AssertEquals("View", (string)detailObjectTypeMethod.Invoke(null, new object[] { "View" }), "Detail panel should support English view type.");
            AssertEquals("Function", (string)detailObjectTypeMethod.Invoke(null, new object[] { "Function" }), "Detail panel should support English function type.");
            AssertEquals("User", (string)detailObjectTypeMethod.Invoke(null, new object[] { "User" }), "Detail panel should support English user type.");
            AssertEquals("Model", (string)detailObjectTypeMethod.Invoke(null, new object[] { "Model" }), "Detail panel should support English model type.");
            AssertEquals("BI", (string)detailObjectTypeMethod.Invoke(null, new object[] { "BI" }), "Detail panel should support English BI type.");
            AssertEquals("Report", (string)detailObjectTypeMethod.Invoke(null, new object[] { "Report" }), "Detail panel should support English report type.");
            AssertEquals("Database: main", (string)sidebarObjectTitleMethod.Invoke(null, new object[] { "Database", "main" }), "Sidebar title should support English database title.");
            AssertEquals("View: public.active_users", (string)sidebarObjectTitleMethod.Invoke(null, new object[] { "View", "public.active_users" }), "Sidebar title should support English view title.");
            AssertEquals("Find in Database: main", (string)sidebarTitleMethod.Invoke(null, new object[] { Localization.T("Database.SearchTitle"), "main" }), "Sidebar title should support English custom labels.");
            AssertEquals("Info", (string)sidebarButtonTextMethod.Invoke(null, new object[] { "Info" }), "Sidebar Info button should support English.");
            AssertEquals("DDL", (string)sidebarButtonTextMethod.Invoke(null, new object[] { "DDL" }), "Sidebar DDL button should support English.");
            AssertEquals("Property", (string)detailGridColumnHeaderMethod.Invoke(null, new object[] { "Key" }), "Detail grid property header should support English.");
            AssertEquals("Value", (string)detailGridColumnHeaderMethod.Invoke(null, new object[] { "Value" }), "Detail grid value header should support English.");
            AssertEquals("Character Set", (string)detailPropertyNameMethod.Invoke(null, new object[] { "CharacterSet" }), "Detail property names should support English character set.");
            AssertEquals("Return Type", (string)detailPropertyNameMethod.Invoke(null, new object[] { "ReturnType" }), "Detail property names should support English return type.");
            AssertEquals("Free Space", (string)detailPropertyNameMethod.Invoke(null, new object[] { "DataFree" }), "Detail property names should support English free space.");
            AssertEquals("Error loading view details: boom", (string)detailLoadErrorMethod.Invoke(null, new object[] { "View", "boom" }), "Detail panel should support English view load errors.");
            AssertEquals("Error loading user details: boom", (string)detailLoadErrorMethod.Invoke(null, new object[] { "User", "boom" }), "Detail panel should support English user load errors.");
            AssertEquals("Function not found: nightly", (string)detailNotFoundMethod.Invoke(null, new object[] { "Function", "nightly" }), "Detail panel should support English function not-found message.");
            AssertEquals("User not found: alice", (string)detailNotFoundMethod.Invoke(null, new object[] { "User", "alice" }), "Detail panel should support English user not-found message.");
            AssertEquals("-- Model: Schema Overview", (string)detailDdlHeaderLineMethod.Invoke(null, new object[] { "Model", "Schema Overview" }), "Detail DDL header should support English object type and separator.");
            AssertEquals("-- Source: metadata", (string)detailDdlCommentLineMethod.Invoke(null, new object[] { "Source", "metadata" }), "Detail DDL comment labels should support English properties.");
            AssertEquals("-- Summarizes table and view column counts, index counts, and row counts.", (string)detailDdlDescriptionLineMethod.Invoke(null, new object[] { (string)modelDescriptionMethod.Invoke(null, new object[] { "Schema Overview" }) }), "Detail DDL description comments should preserve localized English descriptions.");
            AssertEquals("Favorite added: Root\\main", (string)favoriteStatusMethod.Invoke(null, new object[] { "Added", "Root\\main" }), "Favorites should support English added status.");
            AssertEquals("Favorite removed: Root\\main", (string)favoriteStatusMethod.Invoke(null, new object[] { "Removed", "Root\\main" }), "Favorites should support English removed status.");
            AssertEquals("Favorite opened: Root\\main", (string)favoriteStatusMethod.Invoke(null, new object[] { "Opened", "Root\\main" }), "Favorites should support English opened status.");
            AssertEquals("Favorite not found: Missing\\Node", (string)favoriteStatusMethod.Invoke(null, new object[] { "NotFound", "Missing\\Node" }), "Favorites should support English not-found status.");
            AssertEquals("Favorites cleared.", (string)favoriteStatusMethod.Invoke(null, new object[] { "Cleared", "" }), "Favorites should support English cleared status.");
            AssertEquals("Backup created: C:\\backup.sql", (string)backupStatusMethod.Invoke(null, new object[] { "Created", "C:\\backup.sql" }), "Backup status should support English created status.");
            AssertEquals("Backup failed: disk full", (string)backupStatusMethod.Invoke(null, new object[] { "Failed", "disk full" }), "Backup status should support English failed status.");
            AssertEquals("Backup failed: Unknown error", (string)backupStatusMethod.Invoke(null, new object[] { "Failed", "" }), "Blank backup failures should support English unknown errors.");
            AssertEquals("Unknown error", (string)unknownErrorMethod.Invoke(null, new object[0]), "Execution fallback should support English unknown errors.");
            AssertEquals("Unknown error", (string)failureReasonMethod.Invoke(null, new object[] { new Dictionary<string, string>() }), "Missing execution reasons should fall back to English unknown errors.");
            AssertEquals("Import failed: Unknown error", (string)statusExceptionMessageMethod.Invoke(null, new object[] { "Status.ImportFailed", new Exception("   ") }), "Blank import exceptions should localize English unknown errors.");
            AssertEquals("SQL export failed: Unknown error", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Object.SqlExportFailed", new Exception("   ") }), "Blank SQL export exceptions should support English unknown errors.");
            AssertEquals("Copy failed: permission denied", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Object.CopyFailed", new UnauthorizedAccessException(" permission denied ") }), "Formatted exception messages should preserve explicit English reasons.");
            AssertEquals("Failed to load table list: Unknown error", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Database.DataGenerationLoadFailed", new Exception("") }), "Blank data generation load errors should support English unknown errors.");
            AssertEquals("Check for updates failed: Unknown error", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Update.CheckFailed", new Exception("   ") }), "Blank update check errors should support English unknown errors.");
            AssertEquals("Export SQLite column comments failed: Unknown error", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Object.SqliteColumnCommentsExportFailed", new Exception("") }), "Blank SQLite column comment export errors should support English unknown errors.");
            AssertEquals("Backup restore failed: restore denied", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Backup.RestoreFailed", new InvalidOperationException(" restore denied ") }), "Restore errors should preserve explicit English reasons.");
            AssertEquals("Failed to restore quarantined backup: Unknown error", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Backup.QuarantineRestoreFailed", new Exception("") }), "Blank quarantine restore errors should support English unknown errors.");
            AssertEquals("Data generation execution failed: Unknown error", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Database.DataGenerationExecuteFailed", new Exception("   ") }), "Blank generated data execution errors should support English unknown errors.");
            AssertEquals("Delete database failed: drop denied", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Database.DeleteFailed", new InvalidOperationException(" drop denied ") }), "Database delete errors should preserve explicit English reasons.");
            AssertEquals("Create database failed: Unknown error", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Database.CreateFailed", new Exception("") }), "Blank database create errors should support English unknown errors.");
            AssertEquals("Share connection failed: Unknown error", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Connection.ShareFailed", new Exception("") }), "Blank share connection errors should support English unknown errors.");
            AssertEquals("Open command line interface failed: Unknown error", (string)formattedExceptionMessageMethod.Invoke(null, new object[] { "Connection.CommandLineOpenFailed", new Exception("") }), "Blank command line open errors should support English unknown errors.");
            AssertEquals("Scheduled backup integrity check found 1 invalid files. Unknown error", (string)backupIntegrityExceptionStatusMethod.Invoke(null, new object[] { new Exception("   ") }), "Blank backup integrity exceptions should support English unknown errors.");

            DataTable enQueryHistory = (DataTable)queryHistoryMethod.Invoke(form, new object[] { "main" });
            DataRow enQueryHistoryQuery = FindDataRow(enQueryHistory, "SQL", "SELECT 1");
            Assert(enQueryHistoryQuery != null, "Query history should include seeded query entry in English.");
            AssertEquals("Query", enQueryHistoryQuery["類型"].ToString(), "Query history should support English query type.");
            DataRow enQueryHistoryCommand = FindDataRow(enQueryHistory, "SQL", "UPDATE users SET name = 'A'");
            Assert(enQueryHistoryCommand != null, "Query history should include seeded command entry in English.");
            AssertEquals("Command", enQueryHistoryCommand["類型"].ToString(), "Query history should support English command type.");

            DataTable enMaintenance = (DataTable)maintenanceMethod.Invoke(form, new object[] { new FakeDumpDatabase(), "main", new Dictionary<string, object>() });
            DataRow enLargestTable = FindDataRow(enMaintenance, "項目", "Largest Table");
            Assert(enLargestTable != null, "Maintenance checklist should support English item labels.");
            AssertContains(enLargestTable["狀態"].ToString(), "Info", "Maintenance checklist should support English status labels.");
            AssertContains(enLargestTable["說明"].ToString(), "rows", "Maintenance checklist should support English row count detail.");
        }
        finally
        {
            Localization.SetLanguage(oldLanguage, false);
        }
    }

    private static void TestConnectionEditorLocalization()
    {
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            AssertEquals("MySQL 連線失敗：未知錯誤", ConnectionDialogMessageService.BuildTestFailedMessage("MySQL", new Exception("")), "Blank connection test errors should localize Traditional Chinese unknown errors.");
            AssertEquals("SQLite 初始化失敗：未知錯誤", ConnectionDialogMessageService.BuildInitializationFailedMessage("SQLite", new Exception("   ")), "Blank SQLite initialization errors should localize Traditional Chinese unknown errors.");
            using (mySQLPunk.template.mysql_add_edit mysql = new mySQLPunk.template.mysql_add_edit())
            {
                AssertEquals("一般", GetPrivateField<TabPage>(mysql, "tabPage1").Text, "MySQL connection editor tab should localize Traditional Chinese.");
                AssertEquals("連線名稱:", GetPrivateField<Label>(mysql, "label1").Text, "MySQL connection editor name label should localize Traditional Chinese.");
                AssertEquals("主機名稱 / IP 位址:", GetPrivateField<Label>(mysql, "label2").Text, "MySQL connection editor host label should localize Traditional Chinese.");
                AssertEquals("測試連線", GetPrivateField<Button>(mysql, "mysql_add_edit_test_connection").Text, "MySQL connection editor test button should localize Traditional Chinese.");
                AssertEquals("確定", GetPrivateField<Button>(mysql, "mysql_add_edit_ok").Text, "MySQL connection editor OK button should localize Traditional Chinese.");
                AssertEquals("取消", GetPrivateField<Button>(mysql, "mysql_add_edit_cancel").Text, "MySQL connection editor cancel button should localize Traditional Chinese.");
            }

            using (mySQLPunk.template.postgresql_add_edit postgresql = new mySQLPunk.template.postgresql_add_edit())
            {
                AssertEquals("PostgreSQL", postgresql.Text, "PostgreSQL connection editor title should use canonical provider casing.");
                AssertEquals("初始資料庫:", GetPrivateField<Label>(postgresql, "label6").Text, "PostgreSQL connection editor initial database label should localize Traditional Chinese.");
                AssertEquals("使用者名稱:", GetPrivateField<Label>(postgresql, "label4").Text, "PostgreSQL connection editor username label should localize Traditional Chinese.");
                AssertEquals("密碼:", GetPrivateField<Label>(postgresql, "label5").Text, "PostgreSQL connection editor password label should localize Traditional Chinese.");
            }

            using (mySQLPunk.template.oracle_add_edit oracle = new mySQLPunk.template.oracle_add_edit())
            {
                AssertEquals("連線類型:", GetPrivateField<Label>(oracle, "label2").Text, "Oracle connection editor connection type label should localize Traditional Chinese.");
                AssertEquals("服務名稱 / SID:", GetPrivateField<Label>(oracle, "label5").Text, "Oracle connection editor service label should localize Traditional Chinese.");
                AssertEquals("網路服務名稱:", GetPrivateField<Label>(oracle, "label8").Text, "Oracle connection editor net service label should localize Traditional Chinese.");
                AssertEquals("服務名稱", GetPrivateField<RadioButton>(oracle, "radioButton1").Text, "Oracle connection editor service radio should localize Traditional Chinese.");
                AssertEquals("測試連線", GetPrivateField<Button>(oracle, "oracle_add_edit_test_connection").Text, "Oracle connection editor test button should localize Traditional Chinese.");
            }

            using (mySQLPunk.template.sqlserver_add_edit sqlServer = new mySQLPunk.template.sqlserver_add_edit())
            {
                AssertEquals("SQL Server 連線", sqlServer.Text, "SQL Server connection editor title should localize Traditional Chinese.");
                AssertEquals("使用 Windows 驗證", GetPrivateField<CheckBox>(sqlServer, "chkWindowsAuth").Text, "SQL Server Windows auth checkbox should localize Traditional Chinese.");
                AssertEquals("測試連線", GetPrivateField<Button>(sqlServer, "btnTest").Text, "SQL Server connection editor test button should localize Traditional Chinese.");
            }

            Localization.SetLanguage(Localization.English, false);
            AssertEquals("PostgreSQL connection failed: Unknown error", ConnectionDialogMessageService.BuildTestFailedMessage("PostgreSQL", new Exception("")), "Blank connection test errors should localize English unknown errors.");
            AssertEquals("SQL Server connection failed: login denied", ConnectionDialogMessageService.BuildTestFailedMessage("SQL Server", new InvalidOperationException(" login denied ")), "Connection test errors should preserve explicit English reasons.");
            using (mySQLPunk.template.mysql_add_edit mysql = new mySQLPunk.template.mysql_add_edit())
            {
                AssertEquals("General", GetPrivateField<TabPage>(mysql, "tabPage1").Text, "MySQL connection editor tab should support English.");
                AssertEquals("Connection Name:", GetPrivateField<Label>(mysql, "label1").Text, "MySQL connection editor name label should support English.");
                AssertEquals("Host Name/IP Address:", GetPrivateField<Label>(mysql, "label2").Text, "MySQL connection editor host label should support English.");
                AssertEquals("Test Connection", GetPrivateField<Button>(mysql, "mysql_add_edit_test_connection").Text, "MySQL connection editor test button should support English.");
                AssertEquals("OK", GetPrivateField<Button>(mysql, "mysql_add_edit_ok").Text, "MySQL connection editor OK button should support English.");
                AssertEquals("Cancel", GetPrivateField<Button>(mysql, "mysql_add_edit_cancel").Text, "MySQL connection editor cancel button should support English.");
            }

            using (mySQLPunk.template.postgresql_add_edit postgresql = new mySQLPunk.template.postgresql_add_edit())
            {
                AssertEquals("Initial Database:", GetPrivateField<Label>(postgresql, "label6").Text, "PostgreSQL connection editor initial database label should support English.");
                AssertEquals("User Name:", GetPrivateField<Label>(postgresql, "label4").Text, "PostgreSQL connection editor username label should support English.");
                AssertEquals("Password:", GetPrivateField<Label>(postgresql, "label5").Text, "PostgreSQL connection editor password label should support English.");
            }

            using (mySQLPunk.template.oracle_add_edit oracle = new mySQLPunk.template.oracle_add_edit())
            {
                AssertEquals("Connection Type:", GetPrivateField<Label>(oracle, "label2").Text, "Oracle connection editor connection type label should support English.");
                AssertEquals("Service Name/SID:", GetPrivateField<Label>(oracle, "label5").Text, "Oracle connection editor service label should support English.");
                AssertEquals("Net Service Name:", GetPrivateField<Label>(oracle, "label8").Text, "Oracle connection editor net service label should support English.");
                AssertEquals("Service Name", GetPrivateField<RadioButton>(oracle, "radioButton1").Text, "Oracle connection editor service radio should support English.");
            }

            using (mySQLPunk.template.sqlserver_add_edit sqlServer = new mySQLPunk.template.sqlserver_add_edit())
            {
                AssertEquals("SQL Server Connection", sqlServer.Text, "SQL Server connection editor title should support English.");
                AssertEquals("Use Windows Authentication", GetPrivateField<CheckBox>(sqlServer, "chkWindowsAuth").Text, "SQL Server Windows auth checkbox should support English.");
                AssertEquals("Test Connection", GetPrivateField<Button>(sqlServer, "btnTest").Text, "SQL Server connection editor test button should support English.");
            }
        }
        finally
        {
            Localization.SetLanguage(previousLanguage, false);
        }
    }

    private static void TestDatabaseExecutionResultService()
    {
        Localization.SetLanguage(Localization.TraditionalChinese, false);
        Dictionary<string, string> failedWithoutReason = new Dictionary<string, string>
        {
            { "status", "error" }
        };
        AssertContains(DatabaseExecutionResultService.GetFailureReason(failedWithoutReason), "SQL 執行失敗", "SQL execution fallback should localize Traditional Chinese messages.");

        Dictionary<string, string> failedWithEmptyReason = new Dictionary<string, string>
        {
            { "status", "error" },
            { "reason", "  " }
        };
        AssertContains(DatabaseExecutionResultService.GetFailureReason(failedWithEmptyReason), "SQL 執行失敗", "Empty provider reasons should fall back to localized messages.");

        Dictionary<string, string> failedWithReason = new Dictionary<string, string>
        {
            { "status", "error" },
            { "reason", "duplicate key value violates unique constraint" }
        };
        AssertContains(DatabaseExecutionResultService.GetFailureReason(failedWithReason), "duplicate key", "Provider SQL failure reason should be preserved when available.");
        AssertEquals("未知錯誤", ExceptionMessageService.GetReason(new Exception("")), "Blank service exceptions should localize Traditional Chinese unknown errors.");
        AssertEquals("metadata timeout", ExceptionMessageService.GetReason(new InvalidOperationException(" metadata timeout ")), "Service exception helper should trim explicit reasons.");
        AssertEquals("載入 Tables 失敗：未知錯誤", ExceptionMessageService.Format("Metadata.LoadTablesFailed", new Exception("   ")), "Service exception helper should format blank Traditional Chinese reasons.");
        AssertEquals("C:\\runtime\\manifest.json（manifest 無法解析：未知錯誤）", ExceptionMessageService.Format("SpatiaLiteDiagnostics.ManifestParseFailed", @"C:\runtime\manifest.json", new Exception("")), "Service exception helper should format messages with leading arguments.");

        Localization.SetLanguage(Localization.English, false);
        try
        {
            AssertContains(DatabaseExecutionResultService.GetFailureReason(failedWithoutReason), "SQL execution failed", "SQL execution fallback should localize English messages.");
            AssertEquals("Unknown error", ExceptionMessageService.GetReason(new Exception("")), "Blank service exceptions should localize English unknown errors.");
            AssertEquals("Load Views failed: Unknown error", ExceptionMessageService.Format("Metadata.LoadViewsFailed", new Exception("   ")), "Service exception helper should format blank English reasons.");
        }
        finally
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
        }
    }

    private static void TestMySqlGuidFormatNone()
    {
        using (var db = new my_mysql())
        {
            db.SetConn("Server=localhost;User ID=test;Password=test;");
            string connectionString = db.MCT == null ? "" : (db.MCT.ConnectionString ?? "");
            string normalizedConnectionString = connectionString.ToLowerInvariant().Replace(" ", "");
            AssertContains(normalizedConnectionString, "guidformat=none", "MySQL connection should default GuidFormat=None to avoid CHAR(36) parsing failures during dump.");
        }
    }

    private static void TestConnectionProxySettingsService()
    {
        ConnectionProxySettings disabled = new ConnectionProxySettings
        {
            Enabled = false,
            Type = "http",
            Host = "127.0.0.1",
            Port = 8080
        };
        Assert(ConnectionProxySettingsService.CreateWebProxy(disabled) == null, "Disabled proxy should not create a WebProxy.");

        ConnectionProxySettings http = new ConnectionProxySettings
        {
            Enabled = true,
            Type = "http",
            Host = "proxy.local",
            Port = 3128,
            UserName = "user",
            Password = "pass"
        };
        IWebProxy proxy = ConnectionProxySettingsService.CreateWebProxy(http);
        Assert(proxy != null, "HTTP proxy settings should create a WebProxy.");
        Uri proxyUri = proxy.GetProxy(new Uri("http://example.test/"));
        AssertEquals("http://proxy.local:3128/", proxyUri.ToString(), "HTTP proxy URI should include host and port.");
        string oldLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            AssertContains(ConnectionProxySettingsService.BuildStatusText(http), "HTTP 代理 proxy.local:3128", "Proxy status should support Traditional Chinese.");
            AssertContains(ConnectionProxySettingsService.BuildStatusText(disabled), "停用", "Disabled proxy status should support Traditional Chinese.");

            ConnectionProxySettings socks = new ConnectionProxySettings
            {
                Enabled = true,
                Type = "socks5",
                Host = "proxy.local",
                Port = 1080
            };
            Assert(ConnectionProxySettingsService.CreateWebProxy(socks) == null, "SOCKS5 should be stored but not applied to WebRequest.");
            AssertContains(ConnectionProxySettingsService.BuildStatusText(socks), "不支援", "SOCKS5 status should explain WebRequest limitation in Traditional Chinese.");

            ConnectionProxyTestResult directPreflight = ConnectionProxySettingsService.ValidateConnectivityTest(disabled, new Uri("https://example.test/"));
            Assert(directPreflight.Success, "Disabled proxy should allow direct connectivity preflight.");
            Assert(!directPreflight.UsedProxy, "Disabled proxy preflight should not use proxy.");
            AssertContains(directPreflight.Message, "直接連線", "Disabled proxy preflight should describe direct connectivity in Traditional Chinese.");

            ConnectionProxySettings emptyHost = new ConnectionProxySettings
            {
                Enabled = true,
                Type = "http",
                Host = "",
                Port = 8080
            };
            ConnectionProxyTestResult emptyHostPreflight = ConnectionProxySettingsService.ValidateConnectivityTest(emptyHost, new Uri("https://example.test/"));
            Assert(!emptyHostPreflight.Success, "Empty proxy host should fail connectivity preflight.");
            Assert(!emptyHostPreflight.AttemptedRequest, "Invalid proxy settings should not attempt a request.");
            AssertContains(emptyHostPreflight.Message, "主機", "Empty proxy host should return a Traditional Chinese validation message.");

            ConnectionProxyTestResult socksPreflight = ConnectionProxySettingsService.ValidateConnectivityTest(socks, new Uri("https://example.test/"));
            Assert(!socksPreflight.Success, "SOCKS5 should fail WebRequest connectivity preflight.");
            AssertContains(socksPreflight.Message, "SOCKS5", "SOCKS5 preflight should explain limitation.");
            AssertContains(socksPreflight.Message, "HTTP/HTTPS", "SOCKS5 preflight should name the supported proxy types.");
            AssertEquals("連線測試失敗：未知錯誤", ConnectionProxySettingsService.BuildConnectivityFailureMessage(0, new Exception("")), "Blank proxy connectivity errors should localize Traditional Chinese unknown errors.");
            AssertEquals("連線測試失敗，HTTP 502：閘道錯誤", ConnectionProxySettingsService.BuildConnectivityFailureMessage(502, new WebException(" 閘道錯誤 ")), "HTTP proxy connectivity failures should preserve explicit Traditional Chinese reasons.");

            Localization.SetLanguage(Localization.English, false);
            AssertContains(ConnectionProxySettingsService.BuildStatusText(socks), "not supported", "SOCKS5 status should support English.");
            AssertContains(ConnectionProxySettingsService.ValidateConnectivityTest(emptyHost, new Uri("https://example.test/")).Message, "Proxy host is empty", "Empty proxy host should support English.");
            AssertEquals("Connectivity test failed: Unknown error", ConnectionProxySettingsService.BuildConnectivityFailureMessage(0, new Exception("   ")), "Blank proxy connectivity errors should localize English unknown errors.");
        }
        finally
        {
            Localization.SetLanguage(oldLanguage, false);
        }

        HttpWebRequest request = ConnectionProxySettingsService.CreateConnectivityTestRequest(http, new Uri("https://example.test/"), 5000);
        AssertEquals("HEAD", request.Method, "Connectivity test should use HEAD.");
        Assert(request.Proxy != null, "Connectivity request should include HTTP proxy.");
        AssertEquals("http://proxy.local:3128/", request.Proxy.GetProxy(new Uri("https://example.test/")).ToString(), "Connectivity request proxy URI should match settings.");
    }

    private static void TestAdvancedRegistrationService()
    {
        string appPath = @"C:\Program Files\mySQLPunk\mySQLPunk.exe";
        AdvancedRegistrationPlan plan = AdvancedRegistrationService.BuildPlan(appPath, true, true);

        Assert(plan.RegisterSqlOpenWith, "SQL Open With registration should be enabled in the plan.");
        Assert(plan.RegisterUrlProtocol, "URL protocol registration should be enabled in the plan.");
        AssertEquals(appPath, plan.ApplicationPath, "Plan should keep the application path.");
        AssertEquals("\"" + appPath + "\" \"%1\"", plan.OpenWithCommand, "SQL Open With command should quote the executable and SQL file argument.");
        AssertEquals("\"" + appPath + "\" \"%1\"", plan.UrlProtocolCommand, "URL protocol command should quote the executable and URL argument.");
        AssertContains(string.Join("\n", plan.RegistryPaths.ToArray()), @"Software\Classes\Applications\mySQLPunk.exe\SupportedTypes", "Plan should include SQL supported types registry path.");
        AssertContains(string.Join("\n", plan.RegistryPaths.ToArray()), @"Software\Classes\mysqlpunk", "Plan should include URL protocol registry path.");

        AdvancedRegistrationPlan disabled = AdvancedRegistrationService.BuildPlan(appPath, false, false);
        Assert(!disabled.RegisterSqlOpenWith, "Disabled plan should not register SQL Open With.");
        Assert(!disabled.RegisterUrlProtocol, "Disabled plan should not register URL protocol.");
        AssertEquals(0.ToString(), disabled.RegistryPaths.Count.ToString(), "Disabled plan should not report registry paths.");

        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            AssertEquals("套用進階註冊設定失敗：未知錯誤", OptionsForm.BuildAdvancedRegistrationApplyFailedMessage(new Exception("")), "Blank advanced registration apply errors should localize Traditional Chinese unknown errors.");
            AssertEquals("套用進階註冊設定失敗：登錄權限不足", OptionsForm.BuildAdvancedRegistrationApplyFailedMessage(new InvalidOperationException(" 登錄權限不足 ")), "Advanced registration apply errors should preserve explicit Traditional Chinese reasons.");
            try
            {
                AdvancedRegistrationService.Apply(AdvancedRegistrationService.BuildPlan("", true, false));
                Assert(false, "Advanced registration should require an application path.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "請指定應用程式路徑", "Advanced registration should localize Traditional Chinese application path errors.");
            }

            Localization.SetLanguage(Localization.English, false);
            AssertEquals("Failed to apply advanced registration settings: Unknown error", OptionsForm.BuildAdvancedRegistrationApplyFailedMessage(new Exception("   ")), "Blank advanced registration apply errors should localize English unknown errors.");
            try
            {
                AdvancedRegistrationService.Apply(AdvancedRegistrationService.BuildPlan("", true, false));
                Assert(false, "Advanced registration should require an application path in English.");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "Application path is required", "Advanced registration should localize English application path errors.");
            }
        }
        finally
        {
            Localization.SetLanguage(previousLanguage, false);
        }
    }

    private static void TestApplicationUpdateCheckService()
    {
        string releaseJson = @"{
  ""tag_name"": ""v1.2.3"",
  ""name"": ""mySQLPunk 1.2.3"",
  ""html_url"": ""https://github.com/shadowjohn/mySQLPunk/releases/tag/v1.2.3"",
  ""body"": ""更新內容"",
  ""prerelease"": false,
  ""assets"": [
    {
      ""name"": ""mySQLPunk-Setup.exe"",
      ""browser_download_url"": ""https://github.com/shadowjohn/mySQLPunk/releases/download/v1.2.3/mySQLPunk-Setup.exe""
    }
  ]
}";

        AppUpdateCheckResult update = AppUpdateService.ParseGitHubLatestRelease(releaseJson, "1.0.0.0");
        Assert(update.UpdateAvailable, "Update check should detect newer semantic versions.");
        AssertEquals("1.2.3", update.LatestVersion.ToString(), "Update check should normalize v-prefixed release tags.");
        AssertContains(update.ReleasePageUrl, "/releases/tag/v1.2.3", "Update check should keep the release page URL.");
        AssertContains(update.InstallerDownloadUrl, "mySQLPunk-Setup.exe", "Update check should prefer installer assets.");
        AssertContains(update.ReleaseNotes, "更新內容", "Update check should keep release notes.");
        string downloadPath = AppUpdateService.BuildInstallerDownloadPath(update, Path.GetTempPath());
        AssertEquals("mySQLPunk-Setup.exe", Path.GetFileName(downloadPath), "Update download path should keep the installer asset file name.");
        AssertEquals(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetDirectoryName(downloadPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "Update download path should use the requested directory.");

        AppUpdateCheckResult current = AppUpdateService.ParseGitHubLatestRelease(releaseJson, "1.2.3.0");
        Assert(!current.UpdateAvailable, "Update check should not report an update for the same version.");

        string noAssetJson = @"{
  ""tag_name"": ""v1.2.4"",
  ""html_url"": ""https://github.com/shadowjohn/mySQLPunk/releases/tag/v1.2.4"",
  ""assets"": []
}";
        AppUpdateCheckResult noAsset = AppUpdateService.ParseGitHubLatestRelease(noAssetJson, "1.0.0.0");
        AssertEquals("", noAsset.InstallerDownloadUrl, "Update check should not treat the release page as an installer asset.");
        AssertEquals("", noAsset.PortableZipDownloadUrl, "Update check should not invent a portable package when no assets exist.");

        string portableZipJson = @"{
  ""tag_name"": ""v1.2.5"",
  ""html_url"": ""https://github.com/shadowjohn/mySQLPunk/releases/tag/v1.2.5"",
  ""assets"": [
    {
      ""name"": ""mySQLPunk-1.2.5-win-x64-portable.zip"",
      ""browser_download_url"": ""https://github.com/shadowjohn/mySQLPunk/releases/download/v1.2.5/mySQLPunk-1.2.5-win-x64-portable.zip""
    },
    {
      ""name"": ""release-manifest.json"",
      ""browser_download_url"": ""https://github.com/shadowjohn/mySQLPunk/releases/download/v1.2.5/release-manifest.json""
    }
  ]
}";
        AppUpdateCheckResult portableZip = AppUpdateService.ParseGitHubLatestRelease(portableZipJson, "1.0.0.0");
        AssertEquals("", portableZip.InstallerDownloadUrl, "Portable release assets should not be launched as an installer.");
        AssertContains(portableZip.PortableZipDownloadUrl, "mySQLPunk-1.2.5-win-x64-portable.zip", "Portable release assets should be available as downloadable update packages.");
        AssertContains(portableZip.ReleaseManifestDownloadUrl, "release-manifest.json", "Update check should keep the release manifest asset URL.");
        AssertEquals("mySQLPunk-1.2.5-win-x64-portable.zip", AppUpdateService.GetPortableZipFileName(portableZip), "Portable update filename should keep the release asset name.");
        AssertEquals("mySQLPunk-1.2.5-win-x64-portable.zip", Path.GetFileName(AppUpdateService.BuildPortableZipDownloadPath(portableZip, Path.GetTempPath())), "Portable update download path should keep the zip asset file name.");

        string portableScriptZipPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_portable_update_" + Guid.NewGuid().ToString("N") + ".zip");
        string portableScriptDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_portable_update_script_" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(portableScriptZipPath, "zip-content", Encoding.UTF8);
            string scriptPath = AppUpdateService.WritePortableUpdateApplyScript(
                portableScriptZipPath,
                @"C:\Tools\mySQLPunk",
                @"C:\Tools\mySQLPunk\mySQLPunk.exe",
                1234,
                portableScriptDir);
            Assert(File.Exists(scriptPath), "Portable updater should write an apply script.");

            string script = File.ReadAllText(scriptPath, Encoding.UTF8);
            AssertContains(script, "Wait-Process -Id $processIdToWait", "Portable updater should wait for the current process before copying files.");
            AssertContains(script, "Expand-Archive -LiteralPath $zipPath", "Portable updater should extract the downloaded zip.");
            AssertContains(script, "Copy-Item -LiteralPath $_.FullName -Destination $appDir", "Portable updater should copy extracted files into the app directory.");
            AssertContains(script, "Start-Process -FilePath $exePath", "Portable updater should relaunch the application after copying.");

            System.Diagnostics.ProcessStartInfo startInfo = AppUpdateService.BuildPortableUpdateApplyProcessStartInfo(scriptPath);
            AssertEquals("powershell.exe", startInfo.FileName, "Portable updater should launch PowerShell.");
            AssertContains(startInfo.Arguments, "-ExecutionPolicy Bypass", "Portable updater should bypass script policy for the generated script only.");
            AssertContains(startInfo.Arguments, scriptPath, "Portable updater should run the generated script.");
        }
        finally
        {
            if (File.Exists(portableScriptZipPath)) File.Delete(portableScriptZipPath);
            if (Directory.Exists(portableScriptDir)) Directory.Delete(portableScriptDir, true);
        }

        string updatePackagePath = Path.Combine(Path.GetTempPath(), "mysqlpunk_update_hash_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            File.WriteAllText(updatePackagePath, "portable-content", Encoding.UTF8);
            string packageHash = AppUpdateService.ComputeFileSha256(updatePackagePath);
            string manifestJson = @"{
  ""app"": ""mySQLPunk"",
  ""version"": ""1.2.5"",
  ""package"": ""mySQLPunk-1.2.5-win-x64-portable.zip"",
  ""sha256"": """ + packageHash.ToUpperInvariant() + @""",
  ""sizeBytes"": 16
}";
            string expectedHash = AppUpdateService.FindExpectedSha256InReleaseManifest(manifestJson, "mySQLPunk-1.2.5-win-x64-portable.zip");
            AssertEquals(packageHash, expectedHash, "Release manifest parser should return the package SHA-256 for the matching portable zip.");

            string actualHash;
            Assert(AppUpdateService.VerifyFileSha256(updatePackagePath, expectedHash, out actualHash), "Downloaded update package should verify against the release manifest hash.");
            AssertEquals(packageHash, actualHash, "Update package verification should expose the actual SHA-256.");
            Assert(!AppUpdateService.VerifyFileSha256(updatePackagePath, new string('0', 64), out actualHash), "Update package verification should reject mismatched hashes.");
        }
        finally
        {
            if (File.Exists(updatePackagePath)) File.Delete(updatePackagePath);
        }

        AssertEquals(
            "https://api.github.com/repos/shadowjohn/mySQLPunk/releases/latest",
            AppUpdateService.BuildGitHubLatestReleaseApiUrl("shadowjohn", "mySQLPunk"),
            "Update check should build the GitHub latest release endpoint.");

        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            try
            {
                AppUpdateService.BuildGitHubLatestReleaseApiUrl("", "mySQLPunk");
                Assert(false, "Update check should require a GitHub owner.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "請指定 GitHub owner", "Update check should localize Traditional Chinese GitHub owner validation.");
            }

            try
            {
                AppUpdateService.ParseGitHubLatestRelease("", "1.0.0.0");
                Assert(false, "Update check should require release JSON.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "Release JSON 不可為空", "Update check should localize Traditional Chinese release JSON validation.");
            }

            Localization.SetLanguage(Localization.English, false);
            try
            {
                AppUpdateService.BuildGitHubLatestReleaseApiUrl("shadowjohn", "");
                Assert(false, "Update check should require a GitHub repository.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "GitHub repository is required", "Update check should localize English GitHub repository validation.");
            }

            try
            {
                AppUpdateService.BuildPortableUpdateApplyScript("", @"C:\App", @"C:\App\mySQLPunk.exe", 1);
                Assert(false, "Portable updater should require a zip path.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "Portable update zip path is required", "Portable updater should localize English zip path validation.");
            }

            try
            {
                AppUpdateService.BuildPortableUpdateApplyScript(@"C:\Temp\update.zip", "", @"C:\App\mySQLPunk.exe", 1);
                Assert(false, "Portable updater should require an application directory.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "Application directory is required", "Portable updater should localize English app directory validation.");
            }

            try
            {
                AppUpdateService.BuildInstallerDownloadPath(update, "");
                Assert(false, "Update installer download path should require a download directory.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "Download directory is required", "Update check should localize English download directory validation.");
            }

            try
            {
                AppUpdateService.ComputeFileSha256("");
                Assert(false, "Update package hash should require a file path.");
            }
            catch (ArgumentException ex)
            {
                AssertContains(ex.Message, "File path is required", "Update check should localize English file path validation.");
            }
        }
        finally
        {
            Localization.SetLanguage(previousLanguage, false);
        }
    }

    private static void TestApplicationAboutMessage()
    {
        MethodInfo method = typeof(Form1).GetMethod("BuildAboutMessage", BindingFlags.Public | BindingFlags.Static);
        Assert(method != null, "About message builder should be exposed for smoke tests.");

        Type programType = typeof(Form1).Assembly.GetType("mySQLPunk.Program");
        Assert(programType != null, "Program type should be available for smoke tests.");
        MethodInfo unexpectedTitleMethod = programType.GetMethod("BuildUnexpectedErrorTitle", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo unexpectedUiMethod = programType.GetMethod("BuildUnexpectedUiErrorMessage", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo unexpectedBackgroundMethod = programType.GetMethod("BuildUnexpectedBackgroundErrorMessage", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo blobProgressMethod = typeof(QueryForm).GetMethod("BuildBlobStreamingProgressText", BindingFlags.Static | BindingFlags.NonPublic);

        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            string message = (string)method.Invoke(null, new object[] { "1.0.0.0" });
            AssertContains(message, "mySQLPunk", "About message should include the product name.");
            AssertContains(message, "版本：1.0.0.0", "About message should include the current version.");
            AssertContains(message, "支援連線：MySQL、PostgreSQL、SQLite、SQL Server、Oracle", "About message should include supported providers.");
            AssertContains(message, "作者：\r\n羽山秋人 ( https://3wa.tw )\r\nNickYCLin\r\nCodex 協作", "About message should list authors on separate lines.");

            AssertEquals("未預期錯誤", (string)unexpectedTitleMethod.Invoke(null, new object[0]), "Unexpected error title should localize Traditional Chinese.");
            AssertContains((string)unexpectedUiMethod.Invoke(null, new object[] { new InvalidOperationException("boom") }), "執行時發生未預期的錯誤", "UI thread unexpected error should localize Traditional Chinese.");
            AssertContains((string)unexpectedBackgroundMethod.Invoke(null, new object[] { "背景錯誤" }), "背景執行緒發生未預期的錯誤", "Background unexpected error should localize Traditional Chinese.");
            AssertEquals("BLOB 串流匯出中：2 KB / 4 KB", (string)blobProgressMethod.Invoke(null, new object[] { 2048L, 4096L }), "BLOB streaming progress should localize Traditional Chinese.");

            Localization.SetLanguage(Localization.English, false);
            string englishMessage = (string)method.Invoke(null, new object[] { "1.0.0.0" });
            AssertContains(englishMessage, "Version: 1.0.0.0", "About message should support English version text.");
            AssertContains(englishMessage, "Supported connections: MySQL, PostgreSQL, SQLite, SQL Server, Oracle", "About message should support English provider text.");
            AssertContains(englishMessage, "Authors:\r\n羽山秋人 ( https://3wa.tw )\r\nNickYCLin\r\nCodex collaboration", "About message should support English author label.");

            AssertEquals("Unexpected Error", (string)unexpectedTitleMethod.Invoke(null, new object[0]), "Unexpected error title should support English.");
            AssertContains((string)unexpectedUiMethod.Invoke(null, new object[] { new InvalidOperationException("boom") }), "An unexpected error occurred while running", "UI thread unexpected error should support English.");
            AssertContains((string)unexpectedBackgroundMethod.Invoke(null, new object[] { "background failure" }), "An unexpected background error occurred", "Background unexpected error should support English.");
            AssertEquals("Streaming BLOB export: 2 KB / ?", (string)blobProgressMethod.Invoke(null, new object[] { 2048L, -1L }), "BLOB streaming progress should support English.");
        }
        finally
        {
            Localization.SetLanguage(previousLanguage, false);
        }
    }

    private static void TestReleasePackagingScript()
    {
        string root = FindRepositoryRootForTest();
        string scriptPath = Path.Combine(root, "scripts", "package-release.ps1");
        Assert(File.Exists(scriptPath), "Release packaging script should exist.");

        string script = File.ReadAllText(scriptPath, Encoding.UTF8);
        AssertContains(script, "Compress-Archive", "Release packaging script should create a zip archive.");
        AssertContains(script, "release-manifest.json", "Release packaging script should write a release manifest.");
        AssertContains(script, "SHA256", "Release packaging script should include a SHA-256 checksum.");
        AssertContains(script, "mySQLPunk.exe", "Release packaging script should package the application executable.");
        AssertContains(script, "MSBuild", "Release packaging script should build the Release configuration.");
        AssertContains(script, "THIRD_PARTY_NOTICES.md", "Release packaging script should include third-party notices.");
        AssertContains(script, "THIRD_PARTY_LICENSES", "Release packaging script should include bundled license files.");
        AssertContains(script, "Oracle.ManagedDataAccess.23.26.200", "Release packaging script should require the Oracle license file.");
        AssertContains(script, "libreadline8.dll", "Release packaging script should remove GPL Readline from the portable package.");
        AssertContains(script, "libtermcap-0.dll", "Release packaging script should remove the Readline termcap dependency.");
        AssertContains(script, "sqlite3.exe", "Release packaging script should remove the Readline-linked SQLite shell.");

        string projectPath = Path.Combine(root, "mySQLPunk", "mySQLPunk.csproj");
        string project = File.ReadAllText(projectPath, Encoding.UTF8);
        AssertContains(project, "binary\\sqlite3_ext\\sqlite3.exe", "Project should exclude the SQLite shell from release output.");
        AssertContains(project, "binary\\sqlite3_ext\\libreadline*.dll", "Project should exclude Readline from release output.");
        AssertContains(project, "image\\ASSET_NOTICES.md", "Project should copy image asset notices to release output.");

        string spatialiteScriptPath = Path.Combine(root, "tools", "spatialite", "Build-SpatiaLiteRuntime.ps1");
        string spatialiteScript = File.ReadAllText(spatialiteScriptPath, Encoding.UTF8);
        AssertContains(spatialiteScript, "mingw-w64-x86_64-libfreexl", "SpatiaLite rebuild should install the MSYS2 libfreexl package.");
        AssertContains(spatialiteScript, "diffutils make unzip", "SpatiaLite rebuild should install configure-time diff utilities.");
        AssertContains(spatialiteScript, "--host=x86_64-w64-mingw32", "SpatiaLite rebuild should force the MinGW host for libtool.");
        AssertContains(spatialiteScript, "x86_64-w64-mingw32-gcc", "SpatiaLite rebuild should use the MinGW compiler explicitly.");
        AssertContains(spatialiteScript, "LDFLAGS=-no-undefined", "SpatiaLite rebuild should pass the Windows DLL no-undefined linker flag.");
        AssertContains(spatialiteScript, "sed -i 's/ -ldl//g' src/Makefile", "SpatiaLite rebuild should remove the Unix dl library from the MinGW Makefile.");
        AssertContains(spatialiteScript, "make -C src", "SpatiaLite rebuild should build only the runtime source directory and skip examples.");
        AssertContains(spatialiteScript, "/mingw64/lib/mod_spatialite*.dll", "SpatiaLite rebuild should accept libtool's versioned loadable extension output.");
        AssertContains(spatialiteScript, "$msysRuntime/mod_spatialite.dll", "SpatiaLite rebuild should normalize the loadable extension filename for the app runtime.");
        AssertContains(spatialiteScript, "MSYS2 pacman dependency installation failed", "SpatiaLite rebuild should stop when dependency installation fails.");
        AssertContains(spatialiteScript, "Runtime 仍包含不應散布的檔案", "SpatiaLite rebuild should reject blocked runtime files before writing the manifest.");
        AssertContains(spatialiteScript, "Runtime 缺少 mod_spatialite.dll", "SpatiaLite rebuild should require a loadable SpatiaLite extension.");
    }

    private static void TestReleaseThirdPartyNotices()
    {
        string root = FindRepositoryRootForTest();
        string noticesPath = Path.Combine(root, "THIRD_PARTY_NOTICES.md");
        Assert(File.Exists(noticesPath), "Root third-party notices should exist.");

        string notices = File.ReadAllText(noticesPath, Encoding.UTF8);
        AssertContains(notices, "Oracle.ManagedDataAccess", "Third-party notices should document Oracle.ManagedDataAccess.");
        AssertContains(notices, "GNU Readline", "Third-party notices should document the Readline exclusion.");
        AssertContains(notices, "Devicon", "Third-party notices should document Devicon brand icons.");
        AssertContains(notices, "OpenGameArt", "Third-party notices should document progress runner source.");

        string assetNoticesPath = Path.Combine(root, "mySQLPunk", "image", "ASSET_NOTICES.md");
        Assert(File.Exists(assetNoticesPath), "Image asset notices should exist.");
        string assetNotices = File.ReadAllText(assetNoticesPath, Encoding.UTF8);
        AssertContains(assetNotices, "brand_mysql.png", "Image asset notices should list database brand icons.");
        AssertContains(assetNotices, "progress_runner.gif", "Image asset notices should list progress runner animation.");

        string runtimeNoticesPath = Path.Combine(root, "mySQLPunk", "binary", "sqlite3_ext", "THIRD_PARTY_RUNTIME_NOTICES.md");
        Assert(File.Exists(runtimeNoticesPath), "Native runtime notices should exist.");
        string runtimeNotices = File.ReadAllText(runtimeNoticesPath, Encoding.UTF8);
        AssertContains(runtimeNotices, "sqlite3.exe", "Native runtime notices should document the SQLite shell exclusion.");
        AssertContains(runtimeNotices, "libreadline8.dll", "Native runtime notices should document Readline exclusion.");
        AssertContains(runtimeNotices, "SPATIALITE_RUNTIME_MANIFEST.json", "Native runtime notices should require the runtime manifest.");
        AssertContains(runtimeNotices, "RTTOPO", "Native runtime notices should document RTTOPO-related runtime dependencies.");
        AssertContains(runtimeNotices, "GPLv2+", "Native runtime notices should document the RTTOPO/GCP compatibility note.");

        CliPathSettings.SetPath("sqlite", "");
        MethodInfo availabilityMethod = typeof(Form1).GetMethod("GetCliAvailabilityTarget", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(availabilityMethod != null, "CLI availability helper should exist.");
        string sqliteAvailabilityTarget = (string)availabilityMethod.Invoke(null, new object[] { "sqlite" });
        AssertEquals("sqlite3.exe", sqliteAvailabilityTarget, "SQLite CLI availability should use PATH instead of a bundled sqlite3.exe.");
    }

    private static void TestGitHubReleaseWorkflow()
    {
        string root = FindRepositoryRootForTest();
        string workflowPath = Path.Combine(root, ".github", "workflows", "release.yml");
        Assert(File.Exists(workflowPath), "GitHub release workflow should exist.");

        string workflow = File.ReadAllText(workflowPath, Encoding.UTF8);
        AssertContains(workflow, "windows-latest", "Release workflow should build on a Windows runner.");
        AssertContains(workflow, "tags:", "Release workflow should run for pushed version tags.");
        AssertContains(workflow, "workflow_dispatch:", "Release workflow should allow manual releases.");
        AssertContains(workflow, "contents: write", "Release workflow should be allowed to create GitHub Releases.");
        AssertContains(workflow, "nuget restore", "Release workflow should restore packages before building.");
        AssertContains(workflow, "scripts\\package-release.ps1", "Release workflow should use the repository packaging script.");
        AssertContains(workflow, "api.github.com/repos/$env:REPOSITORY/releases", "Release workflow should create or update a GitHub Release through the API.");
        AssertContains(workflow, "${uploadBaseUrl}?name=$assetName", "Release workflow should preserve the upload host when appending asset query parameters.");
    }

    private static string FindRepositoryRootForTest()
    {
        DirectoryInfo current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md")) &&
                File.Exists(Path.Combine(current.FullName, "mySQLPunk.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Cannot find repository root.");
    }

    private static void TestDarkThemeControlCoverage()
    {
        string originalTheme = ThemeManager.CurrentTheme;
        try
        {
            ThemeManager.SetTheme(ThemeManager.Dark, false);
            using (Form form = new Form())
            using (NumericUpDown number = new NumericUpDown())
            using (DateTimePicker dateTimePicker = new DateTimePicker())
            using (DataGridView grid = new DataGridView())
            using (ContextMenuStrip menu = new ContextMenuStrip())
            {
                menu.Items.Add(new ToolStripMenuItem("Open"));
                grid.ContextMenuStrip = menu;
                grid.Columns.Add("name", "name");
                grid.Rows.Add("value");

                form.Controls.Add(number);
                form.Controls.Add(dateTimePicker);
                form.Controls.Add(grid);

                ThemeManager.ApplyTo(form);

                AssertSameColor(ThemeManager.WindowBackColor, form.BackColor, "Dark theme should apply form background.");
                AssertSameColor(ThemeManager.TextBoxBackColor, number.BackColor, "Dark theme should apply numeric input background.");
                AssertSameColor(ThemeManager.TextColor, number.ForeColor, "Dark theme should apply numeric input text.");
                AssertSameColor(ThemeManager.TextBoxBackColor, dateTimePicker.BackColor, "Dark theme should apply date picker background.");
                AssertSameColor(ThemeManager.TextColor, dateTimePicker.ForeColor, "Dark theme should apply date picker text.");
                AssertSameColor(ThemeManager.WindowBackColor, grid.BackgroundColor, "Dark theme should apply grid background.");
                AssertSameColor(ThemeManager.ElevatedColor, grid.RowsDefaultCellStyle.BackColor, "Dark theme should apply grid row background.");
                AssertSameColor(ThemeManager.SelectionColor, grid.DefaultCellStyle.SelectionBackColor, "Dark theme should apply grid selection color.");
                AssertSameColor(ThemeManager.SurfaceColor, menu.BackColor, "Dark theme should apply context menu background.");
                AssertSameColor(ThemeManager.TextColor, menu.Items[0].ForeColor, "Dark theme should apply context menu item text.");
            }
        }
        finally
        {
            ThemeManager.SetTheme(originalTheme, false);
        }
    }

    private static void TestViewColumnPreferenceService()
    {
        string provider = "mysql";
        string group = "Tables";
        string key = "ViewColumns.mysql.Tables";
        string oldValue = ApplicationOptionSettings.GetString(key);

        try
        {
            ApplicationOptionSettings.SetString(key, "");
            List<ViewColumnPreference> defaults = ViewColumnPreferenceService.Load(provider, group);
            Assert(defaults.Count >= 10, "Table column chooser should provide a rich default column list.");
            Assert(defaults.Any(p => p.Name == "名稱") == false, "Table column chooser defaults should match the object metadata columns, not inject grid-only names.");
            Assert(defaults.Any(p => p.Name == "註解" && p.Visible), "Table column chooser should show comments by default.");
            Assert(defaults.First(p => p.Name == "註解").DisplayName == "註解", "Traditional Chinese column chooser should display localized column labels.");
            AssertEquals("mssql", ViewColumnPreferenceService.NormalizeProvider("SQL Server"), "SQL Server provider alias should normalize to mssql.");
            AssertEquals("Views", ViewColumnPreferenceService.NormalizeGroup("View"), "Singular view group should normalize to Views.");
            AssertEquals("資料表空間", ViewColumnPreferenceService.GetGroupDisplayName("Tablespaces"), "Traditional Chinese group labels should be localized.");

            ViewColumnPreferenceService.Save(provider, group, new[]
            {
                new ViewColumnPreference { Name = "註解", Visible = true },
                new ViewColumnPreference { Name = "資料長度", Visible = false },
                new ViewColumnPreference { Name = "引擎", Visible = true }
            });

            List<ViewColumnPreference> saved = ViewColumnPreferenceService.Load(provider, group);
            AssertEquals("註解", saved[0].Name, "Saved column order should be preserved.");
            AssertEquals("註解", saved[0].DisplayName, "Saved column preferences should include localized display names.");
            Assert(saved.First(p => p.Name == "資料長度").Visible == false, "Saved column visibility should be preserved.");
            Assert(saved.Any(p => p.Name == "修改日期"), "Saved preferences should merge newly supported default columns.");

            ViewColumnPreferenceService.Reset(provider, group);
            List<ViewColumnPreference> reset = ViewColumnPreferenceService.Load(provider, group);
            Assert(reset.First(p => p.Name == "資料長度").Visible, "Reset should restore default visible columns.");

            string oldLanguage = Localization.CurrentLanguage;
            try
            {
                Localization.SetLanguage(Localization.English, false);
                List<ViewColumnPreference> englishDefaults = ViewColumnPreferenceService.Load(provider, group);
                AssertEquals("Tablespaces", ViewColumnPreferenceService.GetGroupDisplayName("Tablespaces"), "English group labels should be localized.");
                AssertEquals("Data Length", englishDefaults.First(p => p.Name == "資料長度").DisplayName, "English column chooser should show localized column labels.");
                AssertEquals("Comment", englishDefaults.First(p => p.Name == "註解").DisplayName, "English column chooser should localize comment labels.");
                AssertEquals("資料長度", englishDefaults.First(p => p.Name == "資料長度").Name, "Localized display labels should not replace internal column keys.");
            }
            finally
            {
                Localization.SetLanguage(oldLanguage, false);
            }
        }
        finally
        {
            ApplicationOptionSettings.SetString(key, oldValue);
            ApplicationOptionSettings.Save();
        }
    }

    private static void TestDataViewFilterService()
    {
        DataTable table = new DataTable();
        table.Columns.Add("名稱");
        table.Columns.Add("註解");
        table.Columns.Add("大小%");
        table.Rows.Add("users", "主要使用者資料", "100%");
        table.Rows.Add("logs", "audit trail", "20%");
        table.Rows.Add("orders", "O'Reilly sample", "35%");

        string filter = DataViewFilterService.BuildContainsFilter(table, "使用者");
        table.DefaultView.RowFilter = filter;
        AssertEquals("1", table.DefaultView.Count.ToString(), "Top object filter should match Traditional Chinese text.");
        AssertEquals("users", table.DefaultView[0]["名稱"].ToString(), "Top object filter should keep matching row.");

        table.DefaultView.RowFilter = DataViewFilterService.BuildContainsFilter(table, "O'Reilly");
        AssertEquals("1", table.DefaultView.Count.ToString(), "Top object filter should escape single quotes.");
        AssertEquals("orders", table.DefaultView[0]["名稱"].ToString(), "Top object filter should match escaped quote content.");

        table.DefaultView.RowFilter = DataViewFilterService.BuildContainsFilter(table, "100%");
        AssertEquals("1", table.DefaultView.Count.ToString(), "Top object filter should escape LIKE wildcards.");
        AssertEquals("users", table.DefaultView[0]["名稱"].ToString(), "Top object filter should match literal percent content.");

        AssertEquals("", DataViewFilterService.BuildContainsFilter(table, ""), "Empty top object filter should clear row filter.");
    }

    private static void TestDataViewSortService()
    {
        DataTable table = new DataTable();
        table.Columns.Add("名稱");
        table.Columns.Add("列", typeof(int));
        table.Rows.Add("users", 3);
        table.Rows.Add("logs", 8);
        table.Rows.Add("orders", 1);

        table.DefaultView.Sort = DataViewSortService.BuildSortExpression(table, "列", false);
        AssertEquals("orders", table.DefaultView[0]["名稱"].ToString(), "Ascending object list sort should put the smallest value first.");

        table.DefaultView.Sort = DataViewSortService.BuildSortExpression(table, "列", true);
        AssertEquals("logs", table.DefaultView[0]["名稱"].ToString(), "Descending object list sort should put the largest value first.");

        AssertEquals("", DataViewSortService.BuildSortExpression(table, "不存在", true), "Missing sort column should clear sort expression safely.");
    }

    private static void TestDatabaseGroupVisibilityService()
    {
        Assert(DatabaseGroupVisibilityService.ShouldShowGroup("Functions", 0, false), "Inactive-only off should show empty function group.");
        Assert(!DatabaseGroupVisibilityService.ShouldShowGroup("Functions", 0, true), "Active-only should hide empty function group.");
        Assert(DatabaseGroupVisibilityService.ShouldShowGroup("Functions", 2, true), "Active-only should keep non-empty function group.");
        Assert(DatabaseGroupVisibilityService.ShouldShowGroup("Backups", 0, true), "Active-only should keep action backup group.");
        Assert(DatabaseGroupVisibilityService.ShouldShowGroup("Models", 0, true), "Active-only should keep synthetic model group.");
        Assert(DatabaseGroupVisibilityService.IsKnownGroup("Tables"), "Tables should be a known database tree group.");
        Assert(DatabaseGroupVisibilityService.IsObjectGroup("Views"), "Views should be treated as a database object group.");
        Assert(!DatabaseGroupVisibilityService.IsObjectGroup("Backups"), "Backups should stay as an action group rather than a flattened object group.");
        Assert(DatabaseGroupVisibilityService.ShouldFlattenGroup("Tables", true), "Hide-object-groups should flatten table nodes.");
        Assert(DatabaseGroupVisibilityService.ShouldFlattenGroup("Queries", true), "Hide-object-groups should flatten query nodes.");
        Assert(!DatabaseGroupVisibilityService.ShouldFlattenGroup("Backups", true), "Hide-object-groups should keep backup action nodes grouped.");
        Assert(!DatabaseGroupVisibilityService.ShouldFlattenGroup("Tables", false), "Object groups should stay grouped when the option is disabled.");
    }

    private static void TestWindowsCredentialService()
    {
        Dictionary<string, object> conn = new Dictionary<string, object>
        {
            { "conn_name", "Smoke Test/Connection" },
            { "db_kind", "mysql" },
            { "username", "tester" },
            { "host", "localhost" },
            { "port", "3306" }
        };
        string target = WindowsCredentialService.BuildTargetName("default", conn) + "/" + Guid.NewGuid().ToString("N");
        AssertContains(target, "mySQLPunk/default/mysql/Smoke_Test_Connection/tester@localhost_3306", "Credential target should be deterministic and sanitized.");

        bool wrote = false;
        try
        {
            wrote = WindowsCredentialService.TryWritePassword(target, "tester", "secret-value");
            if (!wrote)
            {
                Console.WriteLine("[WARN] Windows credential store is unavailable in this logon session; skipped write/read/delete round-trip.");
                return;
            }

            string password;
            Assert(WindowsCredentialService.TryReadPassword(target, out password), "Credential service should read a password.");
            AssertEquals("secret-value", password, "Credential service should round-trip the password.");
        }
        finally
        {
            if (wrote)
            {
                Assert(WindowsCredentialService.TryDeletePassword(target), "Credential service should delete the test credential.");
            }
        }
    }

    private static void TestConnectionExportSignatureHelpers()
    {
        MethodInfo computeMethod = typeof(Form1).GetMethod("ComputeConnectionImportSignature", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo readMethod = typeof(Form1).GetMethod("ReadConnectionImportSignature", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo previewMethod = typeof(Form1).GetMethod("BuildConnectionImportPreview", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo summaryMethod = typeof(Form1).GetMethod("BuildConnectionImportSignatureSummary", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo trustSummaryMethod = typeof(Form1).GetMethod("BuildConnectionImportTrustSummary", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo reviewSummaryMethod = typeof(Form1).GetMethod("BuildConnectionImportReviewSummary", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo writeReviewLogMethod = typeof(Form1).GetMethod("WriteConnectionImportReviewLog", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo trustSourceMethod = typeof(Form1).GetMethod("TrustConnectionImportSource", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo isTrustedMethod = typeof(Form1).GetMethod("IsConnectionImportSourceTrusted", BindingFlags.Static | BindingFlags.NonPublic);
        Type reportType = typeof(Form1).GetNestedType("ConnectionImportPreviewReport", BindingFlags.NonPublic);

        string importPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_import_signature_" + Guid.NewGuid().ToString("N") + ".json");
        string trustedSourcesPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_trusted_sources_" + Guid.NewGuid().ToString("N") + ".json");
        string reviewLogPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_import_review_" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            string sourceId = Guid.NewGuid().ToString("N");
            JObject root = JObject.Parse(@"{
  ""connections"": [
    { ""conn_name"": ""signed"", ""db_kind"": ""mysql"", ""host"": ""localhost"", ""port"": ""3306"", ""username"": ""u"" }
  ],
  ""groups"": [""signed-group""],
  ""exportMetadata"": {
    ""formatVersion"": 1,
    ""app"": ""mySQLPunk"",
    ""exportedAtUtc"": ""2026-05-19T08:00:00.0000000Z""
  }
}");
            ((JObject)root["exportMetadata"])["sourceId"] = sourceId;
            string signature = (string)computeMethod.Invoke(null, new object[] { root });
            ((JObject)root["exportMetadata"])["signatureSha256"] = signature;
            File.WriteAllText(importPath, root.ToString(Formatting.Indented), Encoding.UTF8);

            object report = Activator.CreateInstance(reportType, true);
            readMethod.Invoke(null, new object[] { importPath, report });
            Assert((bool)GetProperty(report, "SourceSignaturePresent"), "Signed import should report a source signature.");
            Assert((bool)GetProperty(report, "SourceSignatureValid"), "Signed import should validate unchanged content.");
            AssertEquals(signature, (string)GetProperty(report, "SourceSignature"), "Signed import should keep the source signature.");
            string summary = (string)summaryMethod.Invoke(null, new object[] { report });
            AssertContains(summary, "SHA-256", "Signature summary should show the hash algorithm.");
            string trustSummary = (string)trustSummaryMethod.Invoke(null, new object[] { report });
            AssertContains(trustSummary, "尚未加入白名單", "Untrusted signed source should be called out.");
            object previewReport = previewMethod.Invoke(null, new object[] { importPath, new List<Dictionary<string, object>>() });
            string reviewSummary = (string)reviewSummaryMethod.Invoke(null, new object[] { previewReport });
            AssertContains(reviewSummary, "團隊審核摘要", "Import review should include a team review summary.");
            AssertContains(reviewSummary, "簽章有效但尚未信任", "Import review should describe source trust state.");
            AssertContains(reviewSummary, "新增/更新=1", "Import review should summarize changed connection count.");
            AssertContains(reviewSummary, "需補密碼=1", "Import review should summarize password follow-up count.");
            string writtenReviewLogPath = (string)writeReviewLogMethod.Invoke(null, new object[] { previewReport, "mergeSelected", new[] { 0 }, reviewLogPath });
            AssertEquals(reviewLogPath, writtenReviewLogPath, "Import review log writer should return the written path.");
            Assert(File.Exists(reviewLogPath), "Import review log should be written as JSONL.");
            string reviewLogLine = File.ReadAllLines(reviewLogPath, Encoding.UTF8).Last();
            JObject reviewLog = JObject.Parse(reviewLogLine);
            AssertEquals("mergeSelected", (string)reviewLog["Action"], "Import review log should record the import action.");
            AssertEquals("簽章有效但尚未信任", (string)reviewLog["SourceSignatureState"], "Import review log should record source trust state.");
            Assert((int)reviewLog["SelectedImportedCount"] == 1, "Import review log should record selected imported count.");
            Assert((int)reviewLog["PasswordsRequired"] == 1, "Import review log should record password follow-up count.");
            Assert(reviewLog["Groups"] != null && reviewLog["Groups"].Values<string>().Contains("signed-group"), "Import review log should preserve imported groups.");
            Assert(reviewLog["Targets"] != null && reviewLog["Targets"].HasValues, "Import review log should include changed targets.");
            Assert((bool)reviewLog["Targets"][0]["Selected"], "Import review log should mark selected targets.");
            string reviewLogLanguage = Localization.CurrentLanguage;
            try
            {
                Localization.SetLanguage(Localization.TraditionalChinese, false);
                try
                {
                    writeReviewLogMethod.Invoke(null, new object[] { previewReport, "mergeSelected", new[] { 0 }, "" });
                    Assert(false, "Import review log writer should require a target path.");
                }
                catch (TargetInvocationException ex)
                {
                    ArgumentException argumentException = ex.InnerException as ArgumentException;
                    Assert(argumentException != null, "Import review log writer should throw ArgumentException for empty paths.");
                    AssertContains(argumentException.Message, "請指定匯入審核紀錄路徑", "Import review log path validation should localize Traditional Chinese messages.");
                }
            }
            finally
            {
                Localization.SetLanguage(reviewLogLanguage, false);
            }

            Assert(!(bool)isTrustedMethod.Invoke(null, new object[] { sourceId, trustedSourcesPath }), "New source should not be trusted before whitelisting.");
            trustSourceMethod.Invoke(null, new object[] { sourceId, signature, trustedSourcesPath });
            Assert((bool)isTrustedMethod.Invoke(null, new object[] { sourceId, trustedSourcesPath }), "Trusted source whitelist should persist source IDs.");

            root["connections"][0]["host"] = "changed.example.test";
            File.WriteAllText(importPath, root.ToString(Formatting.Indented), Encoding.UTF8);
            object tamperedReport = Activator.CreateInstance(reportType, true);
            readMethod.Invoke(null, new object[] { importPath, tamperedReport });
            Assert((bool)GetProperty(tamperedReport, "SourceSignaturePresent"), "Tampered import should still report the stored signature.");
            Assert(!(bool)GetProperty(tamperedReport, "SourceSignatureValid"), "Tampered import should fail signature validation.");

            string missingImportPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_missing_import_" + Guid.NewGuid().ToString("N") + ".json");
            string previousLanguage = Localization.CurrentLanguage;
            try
            {
                Localization.SetLanguage(Localization.TraditionalChinese, false);
                try
                {
                    previewMethod.Invoke(null, new object[] { missingImportPath, new List<Dictionary<string, object>>() });
                    Assert(false, "Missing connection import preview should throw.");
                }
                catch (TargetInvocationException ex)
                {
                    FileNotFoundException fileEx = ex.InnerException as FileNotFoundException;
                    Assert(fileEx != null, "Missing connection import preview should throw FileNotFoundException.");
                    AssertContains(fileEx.Message, "找不到連線匯入檔案", "Connection import preview should localize Traditional Chinese missing file errors.");
                }

                Localization.SetLanguage(Localization.English, false);
                try
                {
                    previewMethod.Invoke(null, new object[] { missingImportPath, new List<Dictionary<string, object>>() });
                    Assert(false, "Missing connection import preview should throw in English.");
                }
                catch (TargetInvocationException ex)
                {
                    FileNotFoundException fileEx = ex.InnerException as FileNotFoundException;
                    Assert(fileEx != null, "Missing connection import preview should throw FileNotFoundException in English.");
                    AssertContains(fileEx.Message, "Connection import file not found", "Connection import preview should localize English missing file errors.");
                }
            }
            finally
            {
                Localization.SetLanguage(previousLanguage, false);
            }
        }
        finally
        {
            if (File.Exists(importPath)) File.Delete(importPath);
            if (File.Exists(trustedSourcesPath)) File.Delete(trustedSourcesPath);
            if (File.Exists(reviewLogPath)) File.Delete(reviewLogPath);
        }
    }

    private static void TestConnectionImportPasswordHelpers()
    {
        MethodInfo needsPasswordMethod = typeof(Form1).GetMethod("ConnectionNeedsPasswordAfterImport", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo targetTextMethod = typeof(Form1).GetMethod("BuildImportedConnectionPasswordTargetText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo collectMethod = typeof(Form1).GetMethod("CollectImportedConnectionPasswords", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo previewMethod = typeof(Form1).GetMethod("BuildConnectionImportPreview", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo reviewSummaryMethod = typeof(Form1).GetMethod("BuildConnectionImportReviewSummary", BindingFlags.Static | BindingFlags.NonPublic);

        Dictionary<string, object> mysqlConn = new Dictionary<string, object>
        {
            { "conn_name", "Main MySQL" },
            { "db_kind", "mysql" },
            { "host", "db.example.test" },
            { "port", "3307" },
            { "username", "tester" },
            { "pwd", "" }
        };
        Assert((bool)needsPasswordMethod.Invoke(null, new object[] { mysqlConn }), "Imported MySQL connection without password should be prompted.");
        AssertEquals("db.example.test:3307", (string)targetTextMethod.Invoke(null, new object[] { mysqlConn }), "Imported password target should include host and port.");

        Dictionary<string, object> sqliteConn = new Dictionary<string, object>
        {
            { "db_kind", "sqlite" },
            { "path", "D:\\data\\main.sqlite" },
            { "pwd", "" }
        };
        Assert(!(bool)needsPasswordMethod.Invoke(null, new object[] { sqliteConn }), "SQLite imports should not prompt for a password.");

        Dictionary<string, object> trustedSqlServer = new Dictionary<string, object>
        {
            { "db_kind", "mssql" },
            { "trusted_connection", "T" },
            { "username", "DOMAIN\\tester" },
            { "pwd", "" }
        };
        Assert(!(bool)needsPasswordMethod.Invoke(null, new object[] { trustedSqlServer }), "Trusted SQL Server imports should not prompt for a password.");

        DataTable passwordTable = new DataTable();
        passwordTable.Columns.Add("_index", typeof(int));
        passwordTable.Columns.Add("Password");
        passwordTable.Rows.Add(0, "secret-a");
        passwordTable.Rows.Add(1, "");
        passwordTable.Rows.Add(2, "secret-c");
        var collected = (Dictionary<int, string>)collectMethod.Invoke(null, new object[] { passwordTable });
        Assert(collected.Count == 2 && collected[0] == "secret-a" && collected[2] == "secret-c", "Imported password collector should skip blank rows.");

        string importPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_import_preview_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(importPath, @"{
  ""connections"": [
    { ""conn_name"": ""same"", ""db_kind"": ""mysql"", ""host"": ""localhost"", ""port"": ""3306"", ""username"": ""u"", ""initial_database"": ""main"" },
    { ""conn_name"": ""changed"", ""db_kind"": ""mysql"", ""host"": ""localhost"", ""port"": ""3306"", ""username"": ""u"", ""initial_database"": ""next"" },
    { ""conn_name"": ""new"", ""db_kind"": ""postgresql"", ""host"": ""pg"", ""port"": ""5432"", ""username"": ""pguser"" }
  ],
  ""groups"": [""imported""]
}", Encoding.UTF8);

            List<Dictionary<string, object>> existing = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "conn_name", "same" }, { "db_kind", "mysql" }, { "host", "localhost" }, { "port", "3306" }, { "username", "u" }, { "initial_database", "main" }, { "trusted_connection", "F" }, { "conn_group", "" } },
                new Dictionary<string, object> { { "conn_name", "changed" }, { "db_kind", "mysql" }, { "host", "localhost" }, { "port", "3306" }, { "username", "u" }, { "initial_database", "old" }, { "trusted_connection", "F" }, { "conn_group", "" } },
                new Dictionary<string, object> { { "conn_name", "local-only" }, { "db_kind", "sqlite" }, { "path", "D:\\data\\main.sqlite" }, { "trusted_connection", "F" }, { "conn_group", "" } }
            };

            object report = previewMethod.Invoke(null, new object[] { importPath, existing });
            Assert((int)GetProperty(report, "Added") == 1, "Import preview should count added connections.");
            Assert((int)GetProperty(report, "Updated") == 1, "Import preview should count updated connections.");
            Assert((int)GetProperty(report, "Unchanged") == 1, "Import preview should count unchanged connections.");
            Assert((int)GetProperty(report, "ExistingOnly") == 1, "Import preview should count existing-only connections.");
            var importedGroups = (System.Collections.ICollection)GetProperty(report, "ImportedGroups");
            Assert(importedGroups.Count == 1, "Import preview should keep imported groups for merge.");
            string reviewSummary = (string)reviewSummaryMethod.Invoke(null, new object[] { report });
            AssertContains(reviewSummary, "新增/更新=2", "Import review should count added and updated connections.");
            AssertContains(reviewSummary, "本機只存在=1", "Import review should call out local-only connections before replace-all.");
            AssertContains(reviewSummary, "需補密碼=3", "Import review should count imported connections that need password follow-up.");
            AssertContains(reviewSummary, "imported", "Import review should list imported groups for team review.");
            AssertContains(reviewSummary, "postgresql new@pg:5432", "Import review should include changed targets for reviewers.");
        }
        finally
        {
            if (File.Exists(importPath)) File.Delete(importPath);
        }
    }

    private static DataTable CreateNamedRowsTable(string columnName, string value)
    {
        DataTable table = new DataTable();
        table.Columns.Add(columnName);
        table.Rows.Add(value);
        return table;
    }

    private static string BuildExistingAlterSql(IDatabase db, string databaseName, string tableName, DataTable originalColumns, DataTable currentColumns, string methodName)
    {
        TableDesignerForm form = (TableDesignerForm)FormatterServices.GetUninitializedObject(typeof(TableDesignerForm));
        DataGridView indexesGrid = new DataGridView();
        DataTable indexes = CreateDesignerIndexesTable();
        indexesGrid.DataSource = indexes;

        SetPrivateField(form, "_db", db);
        SetPrivateField(form, "_databaseName", databaseName);
        SetPrivateField(form, "_tableName", tableName);
        SetPrivateField(form, "_originalDt", originalColumns);
        SetPrivateField(form, "_originalIdxDt", indexes.Copy());
        SetPrivateField(form, "dgvIndexes", indexesGrid);

        try
        {
            MethodInfo buildMethod = typeof(TableDesignerForm).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            return (string)buildMethod.Invoke(form, new object[] { currentColumns });
        }
        finally
        {
            indexesGrid.Dispose();
        }
    }

    private static string BuildSqliteAlterColumnUnsupportedMessage()
    {
        TableDesignerForm form = (TableDesignerForm)FormatterServices.GetUninitializedObject(typeof(TableDesignerForm));
        SetPrivateField(form, "_db", new my_sqlite());
        SetPrivateField(form, "_tableName", "demo_table");

        DataTable originalColumns = CreateDesignerColumnsTable();
        AddDesignerColumn(originalColumns, "display_name", "varchar", "50", "", false, false, "", "old comment", "display_name");
        DataTable currentColumns = CreateDesignerColumnsTable();
        AddDesignerColumn(currentColumns, "display_name", "varchar", "120", "", true, false, "unknown", "old comment", "display_name");

        List<string> unsupported = new List<string>();
        MethodInfo buildMethod = typeof(TableDesignerForm).GetMethod("BuildAlterColumnStatements", BindingFlags.Instance | BindingFlags.NonPublic);
        buildMethod.Invoke(form, new object[]
        {
            originalColumns.Rows[0],
            currentColumns.Rows[0],
            "display_name",
            unsupported
        });

        return string.Join("\n", unsupported.ToArray());
    }

    private static string BuildOraclePreviewNotice(string sql, string databaseName, string tableName)
    {
        MethodInfo method = typeof(TableDesignerForm).GetMethod("AddOraclePreviewNotice", BindingFlags.Static | BindingFlags.NonPublic);
        return (string)method.Invoke(null, new object[] { sql, databaseName, tableName });
    }

    private static string BuildOracleHighRiskConfirmationMessage(string sql)
    {
        MethodInfo method = typeof(TableDesignerForm).GetMethod("BuildOracleHighRiskConfirmationMessage", BindingFlags.Static | BindingFlags.NonPublic);
        return (string)method.Invoke(null, new object[] { sql });
    }

    private static string BuildOraclePrivilegeDiagnosticSummary(DataTable objectPrivileges, DataTable sessionPrivileges, string sql)
    {
        MethodInfo method = typeof(TableDesignerForm).GetMethod("BuildOraclePrivilegeDiagnosticSummary", BindingFlags.Static | BindingFlags.NonPublic);
        return (string)method.Invoke(null, new object[] { objectPrivileges, sessionPrivileges, sql });
    }

    private static string BuildOracleRepairSuggestions(string reason, string databaseName, string tableName, string sql, DataTable objectPrivileges, DataTable sessionPrivileges)
    {
        MethodInfo method = typeof(TableDesignerForm).GetMethod("BuildOracleRepairSuggestions", BindingFlags.Static | BindingFlags.NonPublic);
        return (string)method.Invoke(null, new object[] { reason, databaseName, tableName, sql, objectPrivileges, sessionPrivileges });
    }

    private static string BuildOracleDiagnosticFailureMessage(string messageKey, Exception ex)
    {
        MethodInfo method = typeof(TableDesignerForm).GetMethod("BuildOracleDiagnosticFailureMessage", BindingFlags.Static | BindingFlags.NonPublic);
        return (string)method.Invoke(null, new object[] { messageKey, ex });
    }

    private static string BuildOracleErrorHints(string reason, string databaseName, string tableName)
    {
        MethodInfo method = typeof(TableDesignerForm).GetMethod("GetOracleDesignerErrorHints", BindingFlags.Static | BindingFlags.NonPublic);
        IEnumerable<string> hints = (IEnumerable<string>)method.Invoke(null, new object[] { reason, databaseName, tableName });
        return string.Join("\n", hints.ToArray());
    }

    private static DataTable BuildOraclePrivilegeTable(params string[] privileges)
    {
        DataTable table = new DataTable();
        table.Columns.Add("PRIVILEGE");
        foreach (string privilege in privileges)
        {
            table.Rows.Add(privilege);
        }
        return table;
    }

    private static string BuildGenericCreateIndexSql(IDatabase db, string databaseName, string tableName, DataTable indexes)
    {
        TableDesignerForm form = (TableDesignerForm)FormatterServices.GetUninitializedObject(typeof(TableDesignerForm));
        DataGridView indexesGrid = new DataGridView();
        indexesGrid.DataSource = indexes;

        SetPrivateField(form, "_db", db);
        SetPrivateField(form, "_databaseName", databaseName);
        SetPrivateField(form, "_tableName", tableName);
        SetPrivateField(form, "dgvIndexes", indexesGrid);

        try
        {
            MethodInfo buildMethod = typeof(TableDesignerForm).GetMethod("BuildGenericCreateIndexStatements", BindingFlags.Instance | BindingFlags.NonPublic);
            System.Collections.IEnumerable statements = (System.Collections.IEnumerable)buildMethod.Invoke(form, new object[] { tableName });
            StringBuilder builder = new StringBuilder();
            foreach (object statement in statements)
            {
                if (builder.Length > 0) builder.AppendLine();
                builder.Append(statement);
            }
            return builder.ToString();
        }
        finally
        {
            indexesGrid.Dispose();
        }
    }

    private static T CreateProvider<T>() where T : class, IDatabase
    {
        return (T)FormatterServices.GetUninitializedObject(typeof(T));
    }

    private static DataTable CreateOriginalColumnsForAlter(bool includeRemovedColumn)
    {
        DataTable columns = CreateDesignerColumnsTable();
        AddDesignerColumn(columns, "legacy_name", "varchar", "50", "", false, false, "", "old comment", "legacy_name");
        if (includeRemovedColumn)
        {
            AddDesignerColumn(columns, "removed_col", "int", "", "", false, false, "", "", "removed_col");
        }
        return columns;
    }

    private static DataTable CreateChangedColumnsForAlter(bool includeRemovedColumn)
    {
        DataTable columns = CreateDesignerColumnsTable();
        AddDesignerColumn(columns, "display_name", "varchar", "120", "", true, false, "unknown", "display comment", "legacy_name");
        if (includeRemovedColumn)
        {
            AddDesignerColumn(columns, "removed_col", "int", "", "", false, false, "", "", "removed_col");
        }
        AddDesignerColumn(columns, "created_at", "datetime", "", "", false, false, "CURRENT_TIMESTAMP", "created time", "");
        return columns;
    }

    private static DataTable CreateDesignerColumnsTable()
    {
        DataTable columns = new DataTable();
        columns.Columns.Add("Name");
        columns.Columns.Add("Type");
        columns.Columns.Add("Length");
        columns.Columns.Add("Decimals");
        columns.Columns.Add("NotNull", typeof(bool));
        columns.Columns.Add("PK", typeof(bool));
        columns.Columns.Add("Default");
        columns.Columns.Add("Comment");
        columns.Columns.Add("_OldName");
        columns.Columns.Add("_AutoComment", typeof(bool));
        return columns;
    }

    private static void AddDesignerColumn(DataTable table, string name, string type, string length, string decimals, bool notNull, bool primaryKey, string defaultValue, string comment, string oldName)
    {
        DataRow row = table.NewRow();
        row["Name"] = name;
        row["Type"] = type;
        row["Length"] = length;
        row["Decimals"] = decimals;
        row["NotNull"] = notNull;
        row["PK"] = primaryKey;
        row["Default"] = defaultValue;
        row["Comment"] = comment;
        row["_OldName"] = oldName;
        row["_AutoComment"] = false;
        table.Rows.Add(row);
    }

    private static DataTable CreateDesignerIndexesTable()
    {
        DataTable indexes = new DataTable();
        indexes.Columns.Add("名稱");
        indexes.Columns.Add("欄位");
        indexes.Columns.Add("索引類型");
        indexes.Columns.Add("索引方法");
        indexes.Columns.Add("註解");
        indexes.Columns.Add("_OldName");
        return indexes;
    }

    private static void AddDesignerIndex(DataTable table, string name, string columns, string indexType, string indexMethod, string comment)
    {
        DataRow row = table.NewRow();
        row["名稱"] = name;
        row["欄位"] = columns;
        row["索引類型"] = indexType;
        row["索引方法"] = indexMethod;
        row["註解"] = comment;
        row["_OldName"] = name;
        table.Rows.Add(row);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field.SetValue(target, value);
    }

    private static void SeedQueryHistory(object form)
    {
        Type entryType = typeof(Form1).GetNestedType("QueryHistoryEntry", BindingFlags.NonPublic);
        Type listType = typeof(List<>).MakeGenericType(entryType);
        IList entries = (IList)Activator.CreateInstance(listType);
        entries.Add(CreateQueryHistoryEntry(entryType, "main", "SELECT 1", "OK", 1, 12, true));
        entries.Add(CreateQueryHistoryEntry(entryType, "main", "UPDATE users SET name = 'A'", "OK", 1, 18, false));
        SetPrivateField(form, "_queryHistory", entries);
    }

    private static object CreateQueryHistoryEntry(Type entryType, string databaseName, string sql, string status, int rows, long elapsedMilliseconds, bool isQuery)
    {
        object entry = Activator.CreateInstance(entryType, true);
        entryType.GetField("ExecutedAt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(entry, new DateTime(2026, 1, 2, 3, 4, 5));
        entryType.GetField("DatabaseName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(entry, databaseName);
        entryType.GetField("Sql", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(entry, sql);
        entryType.GetField("Status", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(entry, status);
        entryType.GetField("Rows", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(entry, rows);
        entryType.GetField("ElapsedMilliseconds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(entry, elapsedMilliseconds);
        entryType.GetField("IsQuery", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(entry, isQuery);
        return entry;
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return (T)field.GetValue(target);
    }

    private static void SetTextBoxField(object target, string fieldName, string value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        TextBox textBox = (TextBox)field.GetValue(target);
        textBox.Text = value;
    }

    private static void CleanupNamedAutoColumnCommentDictionary(string dictionaryName)
    {
        try
        {
            List<TableDesignerForm.AutoColumnCommentDictionaryVersionInfo> versions =
                TableDesignerForm.ListNamedAutoColumnCommentDictionaryVersions(dictionaryName);
            string versionDirectory = null;
            foreach (TableDesignerForm.AutoColumnCommentDictionaryVersionInfo version in versions)
            {
                if (!string.IsNullOrWhiteSpace(version.FilePath) && File.Exists(version.FilePath))
                {
                    versionDirectory = Path.GetDirectoryName(version.FilePath);
                    File.Delete(version.FilePath);
                }
            }

            if (!string.IsNullOrWhiteSpace(versionDirectory) && Directory.Exists(versionDirectory))
            {
                Directory.Delete(versionDirectory, false);
            }
        }
        catch
        {
        }

        try
        {
            TableDesignerForm.DeleteNamedAutoColumnCommentDictionaryFile(dictionaryName);
        }
        catch
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void AssertEquals(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new Exception(message + " Expected: " + expected + " Actual: " + actual);
        }
    }

    private static void AssertSameColor(Color expected, Color actual, string message)
    {
        if (expected.ToArgb() != actual.ToArgb())
        {
            throw new Exception(message + " Expected: " + expected + " Actual: " + actual);
        }
    }

    private static void AssertContains(string value, string expected, string message)
    {
        if (value == null || value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new Exception(message + " Expected fragment: " + expected + " Actual: " + value);
        }
    }

    private static void AssertNotContains(string value, string unexpected, string message)
    {
        if (value != null && value.IndexOf(unexpected, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new Exception(message + " Unexpected fragment: " + unexpected + " Actual: " + value);
        }
    }

    private static DataRow FindDataRow(DataTable table, string columnName, string value)
    {
        if (table == null || !table.Columns.Contains(columnName)) return null;
        foreach (DataRow row in table.Rows)
        {
            if (string.Equals(row[columnName].ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }
        return null;
    }

    private static string ReadZipEntryText(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName);
        if (entry == null) throw new Exception("Missing zip entry: " + entryName);
        using (Stream stream = entry.Open())
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    private sealed class FakeDumpDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string Provider = "postgresql";
        public List<string> Tables = new List<string> { "public.users" };
        public List<string> Views = new List<string> { "public.active_users" };
        public bool ThrowOnGetTables;
        public string GetTablesExceptionMessage = "table timeout";
        public bool ThrowOnCountRows;
        public string CountRowsExceptionMessage = "row count timeout";
        public string ProviderName => Provider;
        public string ConnectionString;
        public bool WasOpened;

        public void SetConn(string connectionString) { ConnectionString = connectionString; }
        public void Open() { WasOpened = true; }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public System.Threading.Tasks.Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public System.Threading.Tasks.Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public List<string> GetDatabases() { return new List<string> { "main" }; }
        public List<string> GetTables(string databaseName)
        {
            if (ThrowOnGetTables) throw new InvalidOperationException(GetTablesExceptionMessage);
            return Tables;
        }
        public List<string> GetViews(string databaseName) { return Views; }
        public DataTable GetColumns(string databaseName, string tableName)
        {
            DataTable table = new DataTable();
            table.Columns.Add("Field");
            table.Columns.Add("Type");
            table.Columns.Add("Null");
            table.Columns.Add("Key");
            table.Columns.Add("Default");
            table.Rows.Add("id", "integer", "NO", "PRI", "");
            table.Rows.Add("name", "text", "YES", "", "");
            return table;
        }
        public DataTable GetIndexes(string databaseName, string tableName) { return new DataTable(); }
        public DataTable GetTableStatus(string databaseName) { return new DataTable(); }
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) { return new Dictionary<string, string>(); }
        public string GetTableCreateStatement(string databaseName, string tableName) { return "CREATE TABLE \"public\".\"users\" (\"id\" integer, \"name\" text, \"payload\" bytea)"; }
        public bool TableExists(string databaseName, string tableName) { return true; }
        public bool ViewExists(string databaseName, string viewName) { return true; }
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { throw new NotSupportedException(); }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { throw new NotSupportedException(); }
        public long CountRows(string databaseName, string tableName)
        {
            if (ThrowOnCountRows) throw new InvalidOperationException(CountRowsExceptionMessage);
            return 1;
        }
        public DataTable GetCopyColumns(string databaseName, string tableName) { throw new NotSupportedException(); }
        public DataTable GetCopyIndexes(string databaseName, string tableName) { throw new NotSupportedException(); }
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { throw new NotSupportedException(); }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { throw new NotSupportedException(); }
        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            DataTable table = new DataTable();
            table.Columns.Add("id", typeof(int));
            table.Columns.Add("name", typeof(string));
            table.Columns.Add("payload", typeof(byte[]));
            table.Rows.Add(1, "O'Reilly", new byte[] { 0x01, 0x02 });
            return table;
        }
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { throw new NotSupportedException(); }
        public string GetViewCreateStatement(string databaseName, string viewName) { return "CREATE VIEW \"public\".\"active_users\" AS SELECT * FROM \"public\".\"users\""; }
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { throw new NotSupportedException(); }
    }

    private sealed class FakeExecDatabase : IDatabase
    {
        private readonly string providerName;
        private readonly string rowsAffected;
        public List<string> ExecutedSql = new List<string>();
        public List<string> SelectedSql = new List<string>();
        public DataTable SelectResult;

        public FakeExecDatabase(string providerName, string rowsAffected)
        {
            this.providerName = providerName;
            this.rowsAffected = rowsAffected;
        }

        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => providerName;
        public void SetConn(string connectionString) { }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }
        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            SelectedSql.Add(sql);
            return SelectResult == null ? new DataTable() : SelectResult;
        }
        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            ExecutedSql.Add(sql);
            Dictionary<string, string> result = new Dictionary<string, string> { { "status", "OK" } };
            if (rowsAffected != null) result["rowsAffected"] = rowsAffected;
            return result;
        }
        public System.Threading.Tasks.Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public System.Threading.Tasks.Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public List<string> GetDatabases() { return new List<string>(); }
        public List<string> GetTables(string databaseName) { return new List<string>(); }
        public List<string> GetViews(string databaseName) { return new List<string>(); }
        public DataTable GetColumns(string databaseName, string tableName) { return new DataTable(); }
        public DataTable GetIndexes(string databaseName, string tableName) { return new DataTable(); }
        public DataTable GetTableStatus(string databaseName) { return new DataTable(); }
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) { return new Dictionary<string, string>(); }
        public string GetTableCreateStatement(string databaseName, string tableName) { return ""; }
        public bool TableExists(string databaseName, string tableName) { return false; }
        public bool ViewExists(string databaseName, string viewName) { return false; }
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { throw new NotSupportedException(); }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { throw new NotSupportedException(); }
        public long CountRows(string databaseName, string tableName) { return 0; }
        public DataTable GetCopyColumns(string databaseName, string tableName) { throw new NotSupportedException(); }
        public DataTable GetCopyIndexes(string databaseName, string tableName) { throw new NotSupportedException(); }
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { throw new NotSupportedException(); }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { throw new NotSupportedException(); }
        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit) { return new DataTable(); }
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { throw new NotSupportedException(); }
        public string GetViewCreateStatement(string databaseName, string viewName) { return ""; }
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { throw new NotSupportedException(); }
    }

    private sealed class FakeCopyDatabase : IDatabase
    {
        private readonly string providerName;
        private readonly bool includeCopyColumns;
        private readonly string viewSql;
        public bool ThrowOnViewCreateStatement;
        public string ViewCreateStatementExceptionMessage = "view ddl unavailable";

        public FakeCopyDatabase(string providerName, bool includeCopyColumns = false, string viewSql = "CREATE VIEW active_users AS SELECT id FROM users")
        {
            this.providerName = providerName;
            this.includeCopyColumns = includeCopyColumns;
            this.viewSql = viewSql;
        }

        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => providerName;
        public void SetConn(string connectionString) { }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }
        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null) { return new DataTable(); }
        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null) { return new Dictionary<string, string> { { "status", "OK" } }; }
        public System.Threading.Tasks.Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public System.Threading.Tasks.Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public List<string> GetDatabases() { return new List<string> { "main" }; }
        public List<string> GetTables(string databaseName) { return new List<string>(); }
        public List<string> GetViews(string databaseName) { return new List<string>(); }
        public DataTable GetColumns(string databaseName, string tableName) { return new DataTable(); }
        public DataTable GetIndexes(string databaseName, string tableName) { return new DataTable(); }
        public DataTable GetTableStatus(string databaseName) { return new DataTable(); }
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) { return new Dictionary<string, string>(); }
        public string GetTableCreateStatement(string databaseName, string tableName) { return ""; }
        public bool TableExists(string databaseName, string tableName) { return false; }
        public bool ViewExists(string databaseName, string viewName) { return false; }
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { throw new NotSupportedException(); }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { throw new NotSupportedException(); }
        public long CountRows(string databaseName, string tableName) { return 0; }
        public DataTable GetCopyColumns(string databaseName, string tableName)
        {
            DataTable table = new DataTable();
            table.Columns.Add("Name");
            table.Columns.Add("Type");
            if (includeCopyColumns) table.Rows.Add("id", "int");
            return table;
        }
        public DataTable GetCopyIndexes(string databaseName, string tableName) { return new DataTable(); }
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { }
        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit) { return new DataTable(); }
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { }
        public string GetViewCreateStatement(string databaseName, string viewName)
        {
            if (ThrowOnViewCreateStatement) throw new InvalidOperationException(ViewCreateStatementExceptionMessage);
            return viewSql;
        }
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { }
    }

    private sealed class FakeSqliteCommentDatabase : IDatabase
    {
        public List<string> ExecutedSql = new List<string>();
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "sqlite";

        public void SetConn(string connectionString) { }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }
        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            ExecutedSql.Add(sql);
            return new Dictionary<string, string> { { "status", "success" } };
        }
        public System.Threading.Tasks.Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public System.Threading.Tasks.Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public List<string> GetDatabases() { return new List<string> { "main" }; }
        public List<string> GetTables(string databaseName) { return new List<string> { "users", "empty_comments" }; }
        public List<string> GetViews(string databaseName) { return new List<string>(); }
        public DataTable GetColumns(string databaseName, string tableName)
        {
            DataTable table = new DataTable();
            table.Columns.Add("Name");
            table.Columns.Add("Type");
            table.Columns.Add("NotNull");
            table.Columns.Add("Default");
            table.Columns.Add("Comment");
            if (tableName == "users")
            {
                table.Rows.Add("ID", "integer", "1", "", "識別碼");
                table.Rows.Add("NAME", "varchar", "0", "CURRENT_TIMESTAMP", "姓名");
                table.Rows.Add("IGNORED", "text", "0", "", "");
            }
            else
            {
                table.Rows.Add("ID", "integer", "1", "", "");
            }
            return table;
        }
        public DataTable GetIndexes(string databaseName, string tableName) { return new DataTable(); }
        public DataTable GetTableStatus(string databaseName) { return new DataTable(); }
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) { return new Dictionary<string, string>(); }
        public string GetTableCreateStatement(string databaseName, string tableName) { return ""; }
        public bool TableExists(string databaseName, string tableName) { return true; }
        public bool ViewExists(string databaseName, string viewName) { return false; }
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { throw new NotSupportedException(); }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { throw new NotSupportedException(); }
        public long CountRows(string databaseName, string tableName) { return 0; }
        public DataTable GetCopyColumns(string databaseName, string tableName) { throw new NotSupportedException(); }
        public DataTable GetCopyIndexes(string databaseName, string tableName) { throw new NotSupportedException(); }
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { throw new NotSupportedException(); }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { throw new NotSupportedException(); }
        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit) { return new DataTable(); }
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { throw new NotSupportedException(); }
        public string GetViewCreateStatement(string databaseName, string viewName) { return ""; }
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { throw new NotSupportedException(); }
    }
}
