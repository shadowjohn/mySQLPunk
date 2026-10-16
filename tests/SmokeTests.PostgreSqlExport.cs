using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using mySQLPunk.lib.PostgreSqlExport;

public static partial class SmokeTests
{
    private static void PostgreSqlExport_OptionsAndModels_Defaults()
    {
        var options = new PostgreSqlExportWizardOptions(PostgreSqlExportMode.SchemaAndData, @"C:\pg_dump.sql");
        Assert(options.Mode == PostgreSqlExportMode.SchemaAndData, "Default mode should be SchemaAndData");
        Assert(options.UseTransactionSnapshot, "UseTransactionSnapshot should be true by default (REPEATABLE READ)");
        Assert(options.IncludeCreateExtensionPostgis, "IncludeCreateExtensionPostgis should be true by default");
        Assert(options.ExcludeSpatialMetadataTables, "ExcludeSpatialMetadataTables should be true by default");
        Assert(options.IncludeIndexes, "IncludeIndexes should be true by default");
        Assert(options.IncludeComments, "IncludeComments should be true by default");
        Assert(options.IncludeViews, "IncludeViews should be true by default");
        Assert(options.IncludeManifest, "IncludeManifest should be true by default");
        Assert(options.BatchSize == 1000, "Default BatchSize should be 1000");

        var item = new PostgreSqlTableExportItem
        {
            Schema = "public",
            Name = "parcels",
            IsSpatial = true,
            RowCount = 2500,
            SpatialFeatureDescription = "PostGIS Geometry (POLYGON, SRID 3826)"
        };
        Assert(item.FullQualifiedName == "\"public\".\"parcels\"", "FullQualifiedName should match");
        Assert(item.DisplayName == "parcels", "DisplayName for public schema should omit public prefix");

        var customSchemaItem = new PostgreSqlTableExportItem
        {
            Schema = "geo",
            Name = "cadastre"
        };
        Assert(customSchemaItem.FullQualifiedName == "\"geo\".\"cadastre\"", "FullQualifiedName for custom schema");
        Assert(customSchemaItem.DisplayName == "geo.cadastre", "DisplayName for custom schema should include schema prefix");
    }

    private static void PostgreSqlExport_ServiceAndManifest_StreamingAndHexEwkb()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_pg_export_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string targetFile = Path.Combine(tempDir, "pg_export_test.sql");

        try
        {
            var mockDb = new MockPostgreSqlExportDatabase();
            var service = new PostgreSqlSqlExportService(mockDb, "gis_postgres");

            // 1. 驗證 Catalog 正確辨識 PostGIS 空間表與空間中繼表
            var catalog = service.GetTableCatalog();
            Assert(catalog.Count == 2, "Expected 2 objects in catalog, got " + catalog.Count);

            var parcels = catalog.Find(i => i.Name == "parcels");
            Assert(parcels != null, "parcels table should exist in catalog");
            Assert(parcels.IsSpatial, "parcels should be recognized as PostGIS spatial layer");
            Assert(parcels.Selected, "parcels should be selected by default");

            var srs = catalog.Find(i => i.Name == "spatial_ref_sys");
            Assert(srs != null, "spatial_ref_sys table should exist in catalog");
            Assert(srs.IsSpatialMetadata, "spatial_ref_sys should be recognized as spatial metadata");
            Assert(!srs.Selected, "spatial_ref_sys should be unchecked by default to prevent PK conflict on restore");

            var options = new PostgreSqlExportWizardOptions(PostgreSqlExportMode.SchemaAndData, targetFile)
            {
                UseTransactionSnapshot = true,
                IncludeCreateExtensionPostgis = true,
                IncludeIndexes = true,
                IncludeComments = true,
                IncludeManifest = true
            };
            options.SelectedTables.Add("parcels");

            var task = service.ExportAsync(options, null, CancellationToken.None);
            task.Wait();
            var result = task.Result;

            Assert(result.Success, "Export should succeed");
            Assert(File.Exists(targetFile), "Target file must exist after atomic move");
            Assert(result.TableCount == 1, "Exported table count should be 1");
            Assert(result.TotalRows == 2, "Total exported rows should be 2");

            string sql = File.ReadAllText(targetFile);

            // 2. 驗證 PostGIS 空間檔頭宣告與 REPEATABLE READ 交易
            AssertContains(sql, "CREATE EXTENSION IF NOT EXISTS postgis;", "Header must initialize PostGIS extension");
            AssertContains(sql, "BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ;", "Snapshot transaction must be REPEATABLE READ");

            // 3. 驗證 PostGIS 幾何資料轉換為零失真 \\x HEX EWKB ::geometry
            AssertContains(sql, "::geometry", "Geometry values must be formatted as \\x...::geometry");
            AssertContains(sql, "INSERT INTO \"public\".\"parcels\"", "Insert header should target qualified table name");

            // 4. 驗證 GIST 空間索引與結構註解
            AssertContains(sql, "CREATE INDEX idx_parcels_geom ON public.parcels USING gist (geom);", "PostGIS GIST spatial index preserved");
            AssertContains(sql, "COMMENT ON TABLE \"public\".\"parcels\"", "Table comment preserved");

            // 5. 驗證 Native Manifest
            AssertContains(sql, "-- mySQLPunk Native Manifest", "Manifest header present");
            AssertContains(sql, "-- Manifest-Table: parcels | Rows: 2", "Manifest table row count accurate");
            AssertContains(sql, "-- Manifest-Checksum-SHA256:", "Manifest SHA-256 checksum present");
            Assert(!string.IsNullOrWhiteSpace(result.ManifestSha256), "Manifest SHA256 string populated in result");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class MockPostgreSqlExportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "postgresql";
        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            var dt = new DataTable();

            if (sql.Contains("obj_description(c.oid, 'pg_class')"))
            {
                dt.Columns.Add("comment", typeof(string));
                dt.Rows.Add("全國地籍空間圖層");
                return dt;
            }

            if (sql.Contains("FROM pg_class c") && sql.Contains("relkind"))
            {
                dt.Columns.Add("Schema", typeof(string));
                dt.Columns.Add("Name", typeof(string));
                dt.Columns.Add("Kind", typeof(string));
                dt.Columns.Add("EstimatedBytes", typeof(long));
                dt.Columns.Add("RowCount", typeof(long));

                dt.Rows.Add("public", "parcels", "r", 65536L, 2L);
                dt.Rows.Add("public", "spatial_ref_sys", "r", 8500000L, 8500L);
                return dt;
            }

            if (sql.Contains("FROM information_schema.columns c") && sql.Contains("udt_name"))
            {
                dt.Columns.Add("table_schema", typeof(string));
                dt.Columns.Add("table_name", typeof(string));
                dt.Columns.Add("column_name", typeof(string));
                dt.Columns.Add("udt_name", typeof(string));
                dt.Columns.Add("data_type", typeof(string));

                dt.Rows.Add("public", "parcels", "geom", "geometry", "USER-DEFINED");
                return dt;
            }

            if (sql.Contains("FROM geometry_columns"))
            {
                dt.Columns.Add("f_table_schema", typeof(string));
                dt.Columns.Add("f_table_name", typeof(string));
                dt.Columns.Add("f_geometry_column", typeof(string));
                dt.Columns.Add("coord_dimension", typeof(int));
                dt.Columns.Add("srid", typeof(int));
                dt.Columns.Add("type", typeof(string));

                dt.Rows.Add("public", "parcels", "geom", 2, 3826, "MULTIPOLYGON");
                return dt;
            }

            if (sql.Contains("WHERE c.table_schema = :schema AND c.table_name = :tableName"))
            {
                dt.Columns.Add("column_name", typeof(string));
                dt.Columns.Add("data_type", typeof(string));
                dt.Columns.Add("is_nullable", typeof(string));
                dt.Columns.Add("column_default", typeof(string));
                dt.Columns.Add("ProviderType", typeof(string));
                dt.Columns.Add("IsIdentity", typeof(string));
                dt.Columns.Add("IdentityGeneration", typeof(string));
                dt.Columns.Add("IsGenerated", typeof(string));
                dt.Columns.Add("GenerationExpression", typeof(string));
                dt.Columns.Add("Comment", typeof(string));

                dt.Rows.Add("id", "integer", "NO", "nextval('parcels_id_seq'::regclass)", "integer", "NO", "", "NEVER", "", "地籍宗地編號");
                dt.Rows.Add("owner_name", "character varying", "YES", null, "character varying(100)", "NO", "", "NEVER", "", "地主姓名");
                dt.Rows.Add("geom", "USER-DEFINED", "YES", null, "geometry(MultiPolygon,3826)", "NO", "", "NEVER", "", "宗地邊界多邊形");
                return dt;
            }

            if (sql.Contains("SELECT a.attname") && sql.Contains("ix.indisprimary"))
            {
                dt.Columns.Add("attname", typeof(string));
                dt.Rows.Add("id");
                return dt;
            }


            if (sql.Contains("SELECT i.indexdef") && sql.Contains("FROM pg_indexes i"))
            {
                dt.Columns.Add("indexdef", typeof(string));
                dt.Rows.Add("CREATE INDEX idx_parcels_geom ON public.parcels USING gist (geom)");
                return dt;
            }

            if (sql.Contains("SELECT * FROM \"public\".\"parcels\""))
            {
                dt.Columns.Add("id", typeof(int));
                dt.Columns.Add("owner_name", typeof(string));
                dt.Columns.Add("geom", typeof(byte[]));

                byte[] ewkbSample = new byte[] { 0x01, 0x06, 0x00, 0x00, 0x20, 0xF2, 0x0E, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
                dt.Rows.Add(1, "王大明", ewkbSample);
                dt.Rows.Add(2, "林小花", ewkbSample);
                return dt;
            }

            return dt;
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            return new Dictionary<string, string> { { "status", "OK" }, { "rowsAffected", "1" } };
        }

        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(SelectSQL(sql, parameters));
        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(ExecSQL(sql, parameters));
        public List<string> GetDatabases() => new List<string> { "gis_postgres" };
        public List<string> GetTables(string databaseName) => new List<string> { "parcels", "spatial_ref_sys" };
        public List<string> GetViews(string databaseName) => new List<string>();
        public DataTable GetColumns(string databaseName, string tableName) => new DataTable();
        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public DataTable GetTableStatus(string databaseName) => new DataTable();
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();
        public string GetTableCreateStatement(string databaseName, string tableName) => "";
        public bool TableExists(string databaseName, string tableName) => true;
        public bool ViewExists(string databaseName, string viewName) => false;
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { }
        public long CountRows(string databaseName, string tableName) => 2;
        public DataTable GetCopyColumns(string databaseName, string tableName) => new DataTable();
        public DataTable GetCopyIndexes(string databaseName, string tableName) => new DataTable();
        public void SetConn(string connection) { }
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { }
        public void DropTableForCopy(string databaseName, string tableName) { }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { }
        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit) => new DataTable();
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { }
        public string GetViewCreateStatement(string databaseName, string viewName) => "";
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { }
    }
}
