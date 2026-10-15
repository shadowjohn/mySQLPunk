using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using mySQLPunk.lib.OracleExport;

public static partial class SmokeTests
{
    private static void OracleExport_OptionsAndModels_Defaults()
    {
        var options = new OracleExportWizardOptions(OracleExportMode.SchemaAndData, @"C:\oracle_dump.sql");
        Assert(options.Mode == OracleExportMode.SchemaAndData, "Default mode should be SchemaAndData");
        Assert(options.UseTransactionSnapshot, "UseTransactionSnapshot should be true by default (SET TRANSACTION READ ONLY)");
        Assert(options.ExcludeSpatialIndexInternalTables, "ExcludeSpatialIndexInternalTables should be true by default");
        Assert(options.IncludeSpatialMetadata, "IncludeSpatialMetadata should be true by default (USER_SDO_GEOM_METADATA)");
        Assert(options.IncludeSpatialIndexes, "IncludeSpatialIndexes should be true by default (MDSYS.SPATIAL_INDEX)");
        Assert(options.IncludeIndexes, "IncludeIndexes should be true by default");
        Assert(options.IncludeComments, "IncludeComments should be true by default");
        Assert(options.IncludeViews, "IncludeViews should be true by default");
        Assert(options.IncludeManifest, "IncludeManifest should be true by default");
        Assert(options.BatchSize == 500, "Default BatchSize should be 500");

        var item = new OracleTableExportItem
        {
            Owner = "SCOTT",
            Name = "PARCELS",
            IsSpatial = true,
            RowCount = 1200,
            SpatialFeatureDescription = "Oracle Spatial SDO_GEOMETRY"
        };
        Assert(item.FullQualifiedName == "\"SCOTT\".\"PARCELS\"", "FullQualifiedName should match");
        Assert(item.DisplayName == "SCOTT.PARCELS", "DisplayName should match");
    }

    private static void OracleExport_ServiceAndManifest_StreamingAndSdoGeometry()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_ora_export_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string targetFile = Path.Combine(tempDir, "oracle_export_test.sql");

        try
        {
            var mockDb = new MockOracleExportDatabase();
            var service = new OracleSqlExportService(mockDb, "SCOTT");

            // 1. 驗證 Catalog 正確辨識 Oracle Spatial (SDO_GEOMETRY) 空間表與 R-Tree 內部影子表
            var catalog = service.GetTableCatalog();
            Assert(catalog.Count == 2, "Expected 2 objects in catalog, got " + catalog.Count);

            var parcels = catalog.Find(i => i.Name == "PARCELS");
            Assert(parcels != null, "PARCELS table should exist in catalog");
            Assert(parcels.IsSpatial, "PARCELS should be recognized as Oracle Spatial layer");
            Assert(parcels.Selected, "PARCELS should be selected by default");

            var shadow = catalog.Find(i => i.Name == "MDRT_12345$");
            Assert(shadow != null, "MDRT_12345$ table should exist in catalog");
            Assert(shadow.IsSpatialMetadata, "MDRT_* should be recognized as internal spatial shadow table");
            Assert(!shadow.Selected, "MDRT_* should be unchecked by default");

            var options = new OracleExportWizardOptions(OracleExportMode.SchemaAndData, targetFile)
            {
                UseTransactionSnapshot = true,
                IncludeSpatialMetadata = true,
                IncludeSpatialIndexes = true,
                IncludeIndexes = true,
                IncludeComments = true,
                IncludeManifest = true
            };
            options.SelectedTables.Add("PARCELS");

            var task = service.ExportAsync(options, null, CancellationToken.None);
            task.Wait();
            var result = task.Result;

            Assert(result.Success, "Export should succeed");
            Assert(File.Exists(targetFile), "Target file must exist after atomic move");
            Assert(result.TablesExported == 1, "Exported table count should be 1");
            Assert(result.TotalRowsExported == 2, "Total exported rows should be 2");

            string sql = File.ReadAllText(targetFile);

            // 2. 驗證 Oracle SET TRANSACTION READ ONLY 交易快照
            AssertContains(sql, "SET TRANSACTION READ ONLY;", "Header must initialize READ ONLY snapshot");

            // 3. 驗證 USER_SDO_GEOM_METADATA 空間中繼註冊
            AssertContains(sql, "INSERT INTO USER_SDO_GEOM_METADATA", "Must generate USER_SDO_GEOM_METADATA insert");
            AssertContains(sql, "MDSYS.SDO_DIM_ARRAY", "Must generate dimension array");

            // 4. 驗證 SDO_GEOMETRY 幾何資料轉換為無損 SDO_UTIL.FROM_WKBGEOMETRY(HEXTORAW('...'))
            AssertContains(sql, "SDO_UTIL.FROM_WKBGEOMETRY(HEXTORAW(", "Geometry values must be formatted as WKB HEXTORAW");
            AssertContains(sql, "INSERT INTO \"SCOTT\".\"PARCELS\"", "Insert header should target qualified table name");

            // 5. 驗證 Oracle Spatial 空間索引
            AssertContains(sql, "INDEXTYPE IS MDSYS.SPATIAL_INDEX", "Must generate MDSYS.SPATIAL_INDEX domain index");

            // 6. 驗證 Native Manifest
            AssertContains(sql, "-- mySQLPunk Native Manifest", "Manifest header present");
            AssertContains(sql, "-- Manifest-Table: PARCELS | Rows: 2", "Manifest table row count accurate");
            AssertContains(sql, "-- Manifest-Checksum-SHA256:", "Manifest SHA-256 checksum present");
            Assert(!string.IsNullOrWhiteSpace(result.Sha256Checksum), "Manifest SHA256 string populated in result");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class MockOracleExportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "oracle";
        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            var dt = new DataTable();

            if (sql.Contains("FROM ALL_TABLES"))
            {
                dt.Columns.Add("OWNER", typeof(string));
                dt.Columns.Add("TABLE_NAME", typeof(string));
                dt.Columns.Add("ROW_COUNT", typeof(long));
                dt.Columns.Add("ESTIMATED_BYTES", typeof(long));

                dt.Rows.Add("SCOTT", "PARCELS", 2L, 65536L);
                dt.Rows.Add("SCOTT", "MDRT_12345$", 50L, 16384L);
                return dt;
            }

            if (sql.Contains("FROM ALL_VIEWS"))
            {
                dt.Columns.Add("OWNER", typeof(string));
                dt.Columns.Add("VIEW_NAME", typeof(string));
                return dt;
            }

            if (sql.Contains("FROM ALL_TAB_COLUMNS") && sql.Contains("SDO_GEOMETRY"))
            {
                dt.Columns.Add("OWNER", typeof(string));
                dt.Columns.Add("TABLE_NAME", typeof(string));
                dt.Columns.Add("COLUMN_NAME", typeof(string));
                dt.Columns.Add("DATA_TYPE", typeof(string));

                dt.Rows.Add("SCOTT", "PARCELS", "SHAPE", "SDO_GEOMETRY");
                return dt;
            }

            if (sql.Contains("FROM \"SCOTT\".\"PARCELS\""))
            {
                dt.Columns.Add("ID", typeof(int));
                dt.Columns.Add("NAME", typeof(string));
                dt.Columns.Add("SHAPE", typeof(byte[]));

                byte[] wkbSample = new byte[] { 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F };
                dt.Rows.Add(1, "宗地一", wkbSample);
                dt.Rows.Add(2, "宗地二", wkbSample);
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
        public List<string> GetDatabases() => new List<string> { "SCOTT" };
        public List<string> GetTables(string databaseName) => new List<string> { "PARCELS", "MDRT_12345$" };
        public List<string> GetViews(string databaseName) => new List<string>();
        public DataTable GetColumns(string databaseName, string tableName)
        {
            var dt = new DataTable();
            dt.Columns.Add("COLUMN_NAME", typeof(string));
            dt.Columns.Add("DATA_TYPE", typeof(string));
            dt.Rows.Add("ID", "NUMBER");
            dt.Rows.Add("NAME", "VARCHAR2");
            dt.Rows.Add("SHAPE", "SDO_GEOMETRY");
            return dt;
        }
        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public DataTable GetTableStatus(string databaseName) => new DataTable();
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();
        public string GetTableCreateStatement(string databaseName, string tableName)
        {
            return "CREATE TABLE \"SCOTT\".\"PARCELS\" (\"ID\" NUMBER, \"NAME\" VARCHAR2(50), \"SHAPE\" MDSYS.SDO_GEOMETRY);";
        }
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
