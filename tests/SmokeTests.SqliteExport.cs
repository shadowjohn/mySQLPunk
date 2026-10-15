using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using mySQLPunk.lib.SqliteExport;

public static partial class SmokeTests
{
    private static void SqliteExport_OptionsAndModels_Defaults()
    {
        var options = new SqliteExportWizardOptions(SqliteExportMode.SchemaAndData, @"C:\dump.sql");
        Assert(options.Mode == SqliteExportMode.SchemaAndData, "Default mode should be SchemaAndData");
        Assert(options.SingleTransaction, "SingleTransaction should be true by default");
        Assert(options.PragmaForeignKeysOff, "PragmaForeignKeysOff should be true by default");
        Assert(options.PragmaSynchronousOff, "PragmaSynchronousOff should be true by default");
        Assert(options.IncludeDropStatements, "IncludeDropStatements should be true by default");
        Assert(options.IncludeViews, "IncludeViews should be true by default");
        Assert(options.IncludeTriggers, "IncludeTriggers should be true by default");
        Assert(options.IncludeIndexes, "IncludeIndexes should be true by default");
        Assert(options.IncludeComments, "IncludeComments should be true by default");
        Assert(!options.ExcludeSpatialMetadata, "ExcludeSpatialMetadata should be false by default");
        Assert(options.InsertBatchSize == 1000, "Default batch size should be 1000");

        var item = new SqliteTableExportItem("spatial_roads", 1500, true, true, false, false, "[GIS] LineString (SRID 3826)");
        Assert(item.Name == "spatial_roads", "TableName match");
        Assert(item.EstimatedRows == 1500, "EstimatedRows match");
        Assert(item.IsSelected, "IsSelected match");
        Assert(item.IsSpatial, "IsSpatial match");
        Assert(!item.IsSpatialMetadata, "IsSpatialMetadata match");
        AssertContains(item.SpatialInfo, "GIS", "SpatialInfo match");
    }

    private static void SqliteExport_ServiceAndManifest_StreamingAndHexBlob()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_sqlite_export_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string targetFile = Path.Combine(tempDir, "sqlite_export_test.sql");

        try
        {
            var mockDb = new MockSqliteExportDatabase();
            var service = new SqliteSqlExportService(mockDb, "gis_demo.sqlite");

            // 1. 驗證 LoadTableCatalog 正確辨識空間圖層並過濾影子表
            var catalog = service.LoadTableCatalog();
            Assert(catalog.Count == 1, "Catalog should filter out shadow tables like idx_roads_geom_node, expected 1 table but got " + catalog.Count);
            Assert(catalog[0].Name == "spatial_roads", "Export item table name match");
            Assert(catalog[0].IsSpatial, "spatial_roads should be recognized as spatial layer");
            AssertContains(catalog[0].SpatialInfo, "SpatiaLite", "Spatial info contains provider note");

            var options = new SqliteExportWizardOptions(SqliteExportMode.SchemaAndData, targetFile)
            {
                SingleTransaction = true,
                PragmaForeignKeysOff = true,
                PragmaSynchronousOff = true,
                IncludeDropStatements = true,
                IncludeIndexes = true,
                IncludeViews = true,
                IncludeTriggers = true
            };
            options.SelectedTables.Add("spatial_roads");
            options.SelectedViews.Add("v_roads");
            options.SelectedTriggers.Add("trg_roads_audit");

            SqliteExportProgress lastProgress = null;
            var progress = new Progress<SqliteExportProgress>(p => lastProgress = p);

            var task = service.ExportAsync(options, progress, CancellationToken.None);
            task.Wait();
            var result = task.Result;

            Assert(result.Success, "Export should succeed");
            Assert(File.Exists(targetFile), "Target file must exist after atomic move");
            Assert(result.TableCount == 1, "Exported table count should be 1");
            Assert(result.ViewCount == 1, "Exported view count should be 1");
            Assert(result.TriggerCount == 1, "Exported trigger count should be 1");
            Assert(result.IndexCount == 1, "Exported index count should be 1");
            Assert(result.TotalRows == 2, "Exported row count should be 2");

            string content = File.ReadAllText(targetFile);
            AssertContains(content, "-- mySQLPunk SQLite Logical Dump", "Header comment present");
            AssertContains(content, "PRAGMA foreign_keys = OFF;", "Pragma foreign_keys OFF present");
            AssertContains(content, "PRAGMA synchronous = OFF;", "Pragma synchronous OFF present");
            AssertContains(content, "BEGIN TRANSACTION;", "BEGIN TRANSACTION present");
            AssertContains(content, "DROP TABLE IF EXISTS \"spatial_roads\";", "DROP TABLE statement present");
            AssertContains(content, "CREATE TABLE \"spatial_roads\"", "CREATE TABLE DDL present");
            AssertNotContains(content, "CREATE TABLE \"idx_roads_geom_node\"", "Shadow table should NEVER be exported as CREATE TABLE");

            // 驗證 BLOB 幾何二進位輸出為 X'...' 格式，無損精度
            AssertContains(content, "X'00010000007C0101000000'", "GIS binary geometry must be exported as X'...' hex literal");
            AssertContains(content, "COMMIT;", "COMMIT transaction present");
            AssertContains(content, "PRAGMA foreign_keys = ON;", "Restore foreign_keys ON present");

            // 驗證 Native Manifest
            AssertContains(content, "-- mySQLPunk Native Manifest", "Native Manifest header present");
            AssertContains(content, "-- Tables: 1", "Manifest total tables count present");
            AssertContains(content, "-- TotalRows: 2", "Manifest total rows count present");
            AssertContains(content, "-- Manifest-Table: spatial_roads | Rows: 2", "Manifest table row entry present");
            AssertContains(content, "-- Manifest-SHA256:", "Manifest SHA-256 present");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private sealed class MockSqliteExportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "sqlite";

        public void SetConn(string connectionString) { }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            var dt = new DataTable();
            if (sql.Contains("FROM sqlite_master WHERE type='table' AND sql LIKE 'CREATE VIRTUAL%'"))
            {
                dt.Columns.Add("name");
                dt.Rows.Add("idx_roads_geom");
                return dt;
            }
            if (sql.Contains("FROM geometry_columns"))
            {
                dt.Columns.Add("f_table_name");
                dt.Columns.Add("f_geometry_column");
                dt.Columns.Add("type");
                dt.Columns.Add("srid");
                dt.Rows.Add("spatial_roads", "geom", "POINT", "3826");
                return dt;
            }
            if (sql.Contains("FROM gpkg_geometry_columns"))
            {
                return dt;
            }
            if (sql.Contains("SELECT name, sql FROM sqlite_master WHERE type='table'"))
            {
                dt.Columns.Add("name");
                dt.Columns.Add("sql");
                dt.Rows.Add("spatial_roads", "CREATE TABLE \"spatial_roads\" (\"id\" INTEGER PRIMARY KEY, \"name\" TEXT, \"geom\" BLOB);");
                // 影子表 (R-Tree node)
                dt.Rows.Add("idx_roads_geom_node", "CREATE TABLE \"idx_roads_geom_node\" (nodeno INTEGER PRIMARY KEY, data BLOB);");
                return dt;
            }
            if (sql.Contains("WHERE type='table' AND name='spatial_roads'"))
            {
                dt.Columns.Add("sql");
                dt.Rows.Add("CREATE TABLE \"spatial_roads\" (\"id\" INTEGER PRIMARY KEY, \"name\" TEXT, \"geom\" BLOB);");
                return dt;
            }
            if (sql.Contains("SELECT name FROM sqlite_master WHERE type='view'"))
            {
                dt.Columns.Add("name");
                dt.Rows.Add("v_roads");
                return dt;
            }
            if (sql.Contains("WHERE type='view' AND name='v_roads'"))
            {
                dt.Columns.Add("sql");
                dt.Rows.Add("CREATE VIEW \"v_roads\" AS SELECT \"id\", \"name\" FROM \"spatial_roads\";");
                return dt;
            }
            if (sql.Contains("SELECT name FROM sqlite_master WHERE type='trigger'"))
            {
                dt.Columns.Add("name");
                dt.Rows.Add("trg_roads_audit");
                return dt;
            }
            if (sql.Contains("WHERE type='trigger' AND name='trg_roads_audit'"))
            {
                dt.Columns.Add("sql");
                dt.Rows.Add("CREATE TRIGGER \"trg_roads_audit\" AFTER INSERT ON \"spatial_roads\" BEGIN SELECT 1; END;");
                return dt;
            }
            if (sql.Contains("WHERE type='index'"))
            {
                dt.Columns.Add("sql");
                dt.Columns.Add("tbl_name");
                dt.Rows.Add("CREATE INDEX \"idx_roads_name\" ON \"spatial_roads\" (\"name\");", "spatial_roads");
                return dt;
            }
            return dt;
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            return new Dictionary<string, string> { { "status", "OK" } };
        }

        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(SelectSQL(sql, parameters));
        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(ExecSQL(sql, parameters));

        public List<string> GetDatabases() => new List<string> { "main" };
        public List<string> GetTables(string databaseName) => new List<string> { "spatial_roads", "idx_roads_geom_node" };
        public List<string> GetViews(string databaseName) => new List<string> { "v_roads" };

        public DataTable GetColumns(string databaseName, string tableName)
        {
            var dt = new DataTable();
            dt.Columns.Add("COLUMN_NAME");
            dt.Columns.Add("DATA_TYPE");
            dt.Rows.Add("id", "INTEGER");
            dt.Rows.Add("name", "TEXT");
            dt.Rows.Add("geom", "BLOB");
            return dt;
        }

        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public DataTable GetTableStatus(string databaseName) => new DataTable();
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();
        public string GetTableCreateStatement(string databaseName, string tableName) => "CREATE TABLE \"spatial_roads\" (\"id\" INTEGER PRIMARY KEY, \"name\" TEXT, \"geom\" BLOB);";
        public bool TableExists(string databaseName, string tableName) => true;
        public bool ViewExists(string databaseName, string viewName) => true;
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { }
        public long CountRows(string databaseName, string tableName) => 2;
        public DataTable GetCopyColumns(string databaseName, string tableName) => new DataTable();
        public DataTable GetCopyIndexes(string databaseName, string tableName) => new DataTable();
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { }
        public void DropTableForCopy(string databaseName, string tableName) { }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            var dt = new DataTable();
            dt.Columns.Add("id", typeof(int));
            dt.Columns.Add("name", typeof(string));
            dt.Columns.Add("geom", typeof(byte[]));
            if (offset == 0)
            {
                byte[] sampleBlob = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x7C, 0x01, 0x01, 0x00, 0x00, 0x00 };
                dt.Rows.Add(1, "Main Street", sampleBlob);
                dt.Rows.Add(2, "Broadway", sampleBlob);
            }
            return dt;
        }

        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { }
        public string GetViewCreateStatement(string databaseName, string viewName) => "CREATE VIEW \"v_roads\" AS SELECT \"id\", \"name\" FROM \"spatial_roads\";";
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { }
    }
}
