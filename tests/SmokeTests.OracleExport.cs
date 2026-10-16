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

            mockDb.Commands.Clear();
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
            Assert(!sql.Contains("SET TRANSACTION READ ONLY"), "Source read-only transaction must not appear in restore script");
            Assert(mockDb.Commands[0] == "SET TRANSACTION READ ONLY" && mockDb.Commands[mockDb.Commands.Count - 1] == "ROLLBACK", "Real source snapshot surrounds reads and is ended");

            // 3. 驗證 USER_SDO_GEOM_METADATA 空間中繼註冊
            AssertContains(sql, "INSERT INTO USER_SDO_GEOM_METADATA", "Must generate USER_SDO_GEOM_METADATA insert");
            AssertContains(sql, "MDSYS.SDO_DIM_ARRAY", "Must generate dimension array");

            // 4. 驗證 SDO_GEOMETRY 幾何資料轉換為無損 SDO_UTIL.FROM_WKBGEOMETRY(HEXTORAW('...'))
            AssertContains(sql, "SDO_UTIL.FROM_WKBGEOMETRY(TO_BLOB(HEXTORAW(", "Geometry values must be formatted as WKB HEXTORAW");
            AssertContains(sql, "INSERT INTO \"SCOTT\".\"PARCELS\"", "Insert header should target qualified table name");

            // 5. 驗證 Oracle Spatial 空間索引
            AssertContains(sql, "INDEXTYPE IS MDSYS.SPATIAL_INDEX", "Must generate MDSYS.SPATIAL_INDEX domain index");

            // 6. 驗證 Native Manifest
            AssertContains(sql, "-- mySQLPunk Native Manifest", "Manifest header present");
            AssertContains(sql, "-- Manifest-Table: \"SCOTT\".\"PARCELS\" | Rows: 2", "Manifest table row count accurate");
            AssertContains(sql, "-- Manifest-Checksum-SHA256:", "Manifest SHA-256 checksum present");
            Assert(!string.IsNullOrWhiteSpace(result.Sha256Checksum), "Manifest SHA256 string populated in result");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void OracleExport_SpatialMetadataAndOptionGates()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ora_options_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            foreach (int flag in new[] { 0, 1, 2, 3 })
            {
                var db = new MockOracleExportDatabase { VirtualColumn = true };
                string path = Path.Combine(directory, "flags" + flag + ".sql");
                var options = new OracleExportWizardOptions(OracleExportMode.SchemaAndData, path) { IncludeComments = flag == 1, IncludeIndexes = flag == 2, IncludeSpatialIndexes = flag == 3 };
                options.SelectedTables.Add("\"SCOTT\".\"PARCELS\"");
                new OracleSqlExportService(db, "SCOTT").ExportAsync(options).GetAwaiter().GetResult();
                string sql = File.ReadAllText(path);
                Assert(sql.Contains("actual comment") == (flag == 1), "IncludeComments gate");
                Assert(sql.Contains("IX_NAME") == (flag == 2), "IncludeIndexes gate");
                Assert(sql.Contains("REAL_SPATIAL_INDEX") == (flag == 3), "IncludeSpatialIndexes gate preserves actual name");
                Assert(!sql.Contains("SIDX_PARCELS_SHAPE") && !sql.Contains("4326"), "No fabricated spatial index or SRID");
                Assert(sql.Contains("'E', 0, 1000000, 0.01") && sql.Contains("'Z', -100, 9000, 0.1"), "Actual three-dimensional bounds and tolerance preserved");
                Assert(sql.Contains(", 3826)") && sql.Contains(", NULL)"), "Per-value projected and null SRIDs preserved");
                Assert(!db.Commands.Exists(command => command.StartsWith("SELECT ") && command.Contains("CALC")), "Virtual column omitted from reader projection");
                if (flag == 3) Assert(sql.IndexOf("INSERT INTO USER_SDO_GEOM_METADATA") < sql.IndexOf("REAL_SPATIAL_INDEX"), "Spatial metadata precedes actual spatial index");
            }
            var nullDb = new MockOracleExportDatabase { NullMetadataDimensions = true, GeometrySrid = null };
            var nullOptions = new OracleExportWizardOptions(OracleExportMode.SchemaOnly, Path.Combine(directory, "null.sql")); nullOptions.SelectedTables.Add("PARCELS");
            new OracleSqlExportService(nullDb, "SCOTT").ExportAsync(nullOptions).GetAwaiter().GetResult();
            Assert(File.ReadAllText(nullOptions.TargetPath).Contains("'SHAPE', NULL, NULL)"), "NULL DIMINFO and metadata SRID retained");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void OracleExport_BoundedLobsSnapshotFailureAndCancel()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ora_lobs_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "dump.sql");
        try
        {
            var db = new MockOracleExportDatabase { TextValue = new string('中', 5000), GeometryBytes = 3000 };
            var options = new OracleExportWizardOptions(OracleExportMode.DataOnly, path) { BatchSize = 1 }; options.SelectedTables.Add("PARCELS");
            bool outputBeforeExhaustion = false;
            var observer = new OracleProgressObserver(p =>
            {
                if (p.RowsExported == 1)
                {
                    string[] staging = Directory.GetFiles(directory, "dump.sql.*.writing");
                    if (staging.Length == 1)
                        using (var pending = new StreamReader(new FileStream(staging[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                            outputBeforeExhaustion = pending.ReadToEnd().Contains("DBMS_LOB.WRITEAPPEND") && db.Reader != null && !db.Reader.IsClosed;
                }
            });
            new OracleSqlExportService(db, "SCOTT").ExportAsync(options, observer).GetAwaiter().GetResult();
            Assert(outputBeforeExhaustion, "First row is written and flushed while source reader remains active");
            string sql = File.ReadAllText(path);
            Assert(sql.Contains("DBMS_LOB.WRITEAPPEND") && sql.Contains(" BLOB;") && sql.Contains(" CLOB;"), "Large WKB/CLOB uses bounded temporary LOB blocks");
            Assert(!sql.Contains("SET TRANSACTION READ ONLY"), "Data-only restore remains writable");
            foreach (System.Text.RegularExpressions.Match literal in System.Text.RegularExpressions.Regex.Matches(sql, @"HEXTORAW\('([0-9A-F]+)'\)")) Assert(literal.Groups[1].Length <= 4000, "Every binary hex literal stays within STANDARD limit");
            string parsePath = Path.Combine(directory, "parse.sql"); File.WriteAllText(parsePath, sql);
            var preflight = new mySQLPunk.lib.OracleImport.OracleImportPreflightScanner().Scan(parsePath);
            Assert(preflight.CanProceed && preflight.HasPlSqlBlocks, "Real exporter LOB blocks parse as complete executable statements");
            var restore = new MockOracleImportDatabase { ActualRows = 2 };
            var restored = new mySQLPunk.lib.OracleImport.OracleImportRunner(restore, new mySQLPunk.lib.OracleImport.OracleImportRequest(parsePath, "SCOTT")).Run();
            Assert(restored.Success && restore.ExecutedQueries.Exists(command => command.StartsWith("DECLARE")), "Actual generated LOB script passes owner validation and executes as whole blocks (recording provider only)");
            foreach (string failure in new[] { "SET TRANSACTION", "ROLLBACK" })
            {
                File.WriteAllText(path, "KEEP ORIGINAL");
                var failing = new MockOracleExportDatabase { FailCommand = failure };
                bool threw = false;
                try { new OracleSqlExportService(failing, "SCOTT").ExportAsync(options).GetAwaiter().GetResult(); } catch { threw = true; }
                Assert(threw && File.ReadAllText(path) == "KEEP ORIGINAL", "Source command NO prevents publication: " + failure);
            }
            using (var cancel = new CancellationTokenSource())
            {
                var cancelled = new MockOracleExportDatabase { BeforeReaderOpen = () => cancel.Cancel() };
                File.WriteAllText(path, "KEEP ORIGINAL"); bool threw = false;
                try { new OracleSqlExportService(cancelled, "SCOTT").ExportAsync(options, null, cancel.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { threw = true; }
                Assert(threw && cancelled.Commands.Contains("ROLLBACK") && File.ReadAllText(path) == "KEEP ORIGINAL", "Cancellation ends source transaction and preserves existing dump");
            }
            var empty = new MockOracleExportDatabase { EmptyLobs = true };
            new OracleSqlExportService(empty, "SCOTT").ExportAsync(options).GetAwaiter().GetResult();
            string emptySql = File.ReadAllText(path);
            Assert(emptySql.Contains("EMPTY_BLOB()") && emptySql.Contains("EMPTY_CLOB()") && emptySql.Contains("TO_BLOB(HEXTORAW("), "Empty non-null LOBs remain distinct from NULL and small geometry uses explicit BLOB conversion");
            var tooLarge = new MockOracleExportDatabase { GeometryBytes = OracleSqlExportService.MaxRowValueBytes + 1 };
            File.WriteAllText(path, "KEEP ORIGINAL"); bool overBudget = false;
            try { new OracleSqlExportService(tooLarge, "SCOTT").ExportAsync(options).GetAwaiter().GetResult(); } catch (NotSupportedException) { overBudget = true; }
            Assert(overBudget && File.ReadAllText(path) == "KEEP ORIGINAL", "Row budget failure preserves previous output");
            var identity = new MockOracleExportDatabase { Identity = true }; bool rejected = false;
            try { new OracleSqlExportService(identity, "SCOTT").ExportAsync(options).GetAwaiter().GetResult(); } catch (NotSupportedException) { rejected = true; }
            Assert(rejected, "Identity data rejected explicitly until sequence-state restoration verified");
            foreach (bool cycle in new[] { false, true })
            {
                var dependency = new MockOracleExportDatabase { ForeignKeyCycle = cycle, MissingForeignKeyParent = !cycle };
                File.WriteAllText(path, "KEEP ORIGINAL"); bool refused = false;
                try { new OracleSqlExportService(dependency, "SCOTT").ExportAsync(options).GetAwaiter().GetResult(); } catch (NotSupportedException) { refused = true; }
                Assert(refused && File.ReadAllText(path) == "KEEP ORIGINAL", "Selected FK cycle or absent parent is rejected before publication");
            }
            bool oversized = false;
            try { OracleSqlExportService.FormatOracleLiteral(new string('中', 2000), false); } catch (NotSupportedException) { oversized = true; }
            Assert(oversized, "Scalar UTF-8 literal bound is measured in bytes");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void OracleExport_ScopedBinaryFloatRoundTrip()
    {
        foreach (double value in new[] { 1.2345678901234567d, 1.7e308d, double.Epsilon, 0d })
        {
            string literal = OracleSqlExportService.FormatOracleLiteral(value, false);
            Assert(literal == value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + "d", "BINARY_DOUBLE uses exact G17 typed token");
            double parsed = double.Parse(literal.Substring(0, literal.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
            Assert(BitConverter.DoubleToInt64Bits(parsed) == BitConverter.DoubleToInt64Bits(value), "Double token roundtrips exactly in .NET");
        }
        foreach (float value in new[] { 1.2345678f, 1.7e30f, float.Epsilon, 0f })
        {
            string literal = OracleSqlExportService.FormatOracleLiteral(value, false);
            Assert(literal == value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture) + "f", "BINARY_FLOAT uses exact G9 typed token");
            float parsed = float.Parse(literal.Substring(0, literal.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
            Assert(BitConverter.ToInt32(BitConverter.GetBytes(parsed), 0) == BitConverter.ToInt32(BitConverter.GetBytes(value), 0), "Float token roundtrips exactly in .NET");
        }
        foreach (object value in new object[] { BitConverter.Int64BitsToDouble(long.MinValue), BitConverter.ToSingle(BitConverter.GetBytes(int.MinValue), 0) })
        {
            bool rejected = false; try { OracleSqlExportService.FormatOracleLiteral(value, false); } catch (NotSupportedException) { rejected = true; }
            Assert(rejected, "Signed zero rejects until native storage fidelity is verified");
        }
        string path = Path.Combine(Path.GetTempPath(), "ora_float_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            var source = new MockOracleExportDatabase { FloatingValues = true };
            var options = new OracleExportWizardOptions(OracleExportMode.DataOnly, path); options.SelectedTables.Add("PARCELS");
            new OracleSqlExportService(source, "SCOTT").ExportAsync(options).GetAwaiter().GetResult();
            string dump = File.ReadAllText(path);
            Assert(!dump.Contains(((double)1.2345678f).ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + "d"), "Float is emitted as float rather than widened double");
            Assert(dump.Contains((1.2345678f).ToString("G9", System.Globalization.CultureInfo.InvariantCulture) + "f") && dump.Contains("1.2345678901234567d") && dump.Contains((1.7e308d).ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + "d"), "Recording reader retains finite high precision/exponent binary values");
            File.WriteAllText(path, "KEEP ORIGINAL"); source.NegativeZero = true; bool rejected = false;
            try { new OracleSqlExportService(source, "SCOTT").ExportAsync(options).GetAwaiter().GetResult(); } catch (NotSupportedException) { rejected = true; }
            Assert(rejected && File.ReadAllText(path) == "KEEP ORIGINAL", "Signed-zero source failure prevents publication");
        }
        finally { File.Delete(path); }
    }

    private static void OracleExport_ScopedNativeViewsAndDisabledForeignKeys()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ora_view_fk_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "schema.sql");
        try
        {
            var source = new MockOracleExportDatabase { NativeView = true };
            var options = new OracleExportWizardOptions(OracleExportMode.SchemaOnly, path); options.SelectedTables.Add("PARCELS"); options.SelectedTables.Add("V");
            new OracleSqlExportService(source, "SCOTT").ExportAsync(options).GetAwaiter().GetResult();
            string dump = File.ReadAllText(path);
            Assert(dump.Contains("CREATE OR REPLACE FORCE EDITIONABLE VIEW \"SCOTT\".\"V\""), "Representative native view DDL exported");
            var destination = new MockOracleImportDatabase();
            Assert(new mySQLPunk.lib.OracleImport.OracleImportRunner(destination, new mySQLPunk.lib.OracleImport.OracleImportRequest(path, "SCOTT")).Run().Success, "Exported native view grammar passes real import prepass");
            foreach (string changed in new[] { dump.Replace("VIEW \"SCOTT\".\"V\"", "VIEW \"A\".\"V\""), dump.Replace("SELECT \"ID\" FROM \"SCOTT\".\"PARCELS\"", "SELECT \"ID\" FROM \"A\".\"PARCELS\"") })
            {
                File.WriteAllText(path, changed); var rejected = new MockOracleImportDatabase();
                Assert(!new mySQLPunk.lib.OracleImport.OracleImportRunner(rejected, new mySQLPunk.lib.OracleImport.OracleImportRequest(path, "SCOTT")).Run().Success && rejected.ExecutedQueries.Count == 0, "Native view owner/reference mismatch fails before session/mutations");
            }
            source.JoinedNativeView = true;
            new OracleSqlExportService(source, "SCOTT").ExportAsync(options).GetAwaiter().GetResult();
            var joined = new MockOracleImportDatabase();
            Assert(new mySQLPunk.lib.OracleImport.OracleImportRunner(joined, new mySQLPunk.lib.OracleImport.OracleImportRequest(path, "SCOTT")).Run().Success, "Native FORCE/EDITIONABLE joined view does not mistake ON aliases for owners");
            string joinedDump = File.ReadAllText(path).Replace("JOIN \"SCOTT\".\"PARCELS\" b", "JOIN \"A\".\"PARCELS\" b");
            File.WriteAllText(path, joinedDump); var joinedWrongOwner = new MockOracleImportDatabase();
            Assert(!new mySQLPunk.lib.OracleImport.OracleImportRunner(joinedWrongOwner, new mySQLPunk.lib.OracleImport.OracleImportRequest(path, "SCOTT")).Run().Success && joinedWrongOwner.ExecutedQueries.Count == 0, "Joined native view still validates actual referenced owner");
            var dependency = new MockOracleExportDatabase { DisabledForeignKey = true, AddParent = true };
            var schema = new OracleExportWizardOptions(OracleExportMode.SchemaOnly, path); schema.SelectedTables.Add("PARCELS"); schema.SelectedTables.Add("PARENT");
            new OracleSqlExportService(dependency, "SCOTT").ExportAsync(schema).GetAwaiter().GetResult();
            string ordered = File.ReadAllText(path);
            Assert(ordered.IndexOf("CREATE TABLE \"SCOTT\".\"PARENT\"", StringComparison.Ordinal) < ordered.IndexOf("CREATE TABLE \"SCOTT\".\"PARCELS\"", StringComparison.Ordinal), "Disabled FK parent DDL precedes child DDL");
            Assert(ordered.Contains("DISABLE") && dependency.Commands.Exists(command => command.Contains("FROM ALL_CONSTRAINTS c JOIN") && !command.Contains("c.STATUS='ENABLED'")), "All schema FK definitions participate, preserving disabled DDL");
            foreach (int failure in new[] { 0, 1, 2 })
            {
                var refused = new MockOracleExportDatabase { DisabledForeignKey = true, AddParent = failure != 0, CrossOwnerForeignKey = failure == 1, ForeignKeyCycle = failure == 2 };
                File.WriteAllText(path, "KEEP ORIGINAL"); bool rejected = false;
                try { new OracleSqlExportService(refused, "SCOTT").ExportAsync(schema).GetAwaiter().GetResult(); } catch (NotSupportedException) { rejected = true; }
                Assert(rejected && File.ReadAllText(path) == "KEEP ORIGINAL", "Disabled missing/cross-owner/cyclic FK rejects before publication");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    // DataTable's DoubleStorage normalizes signed zero. This recording reader returns the provider bit pattern explicitly.
    private sealed class OracleSignedZeroReader : IDataReader
    {
        private readonly IDataReader inner;
        public OracleSignedZeroReader(IDataReader inner) { this.inner = inner; }
        public object GetValue(int i) { object value = inner.GetValue(i); return i == 1 && value is double && (double)value == 0d ? BitConverter.Int64BitsToDouble(long.MinValue) : value; }
        public object this[int i] => GetValue(i);
        public object this[string name] => inner[name];
        public int Depth => inner.Depth;
        public bool IsClosed => inner.IsClosed;
        public int RecordsAffected => inner.RecordsAffected;
        public int FieldCount => inner.FieldCount;
        public void Close() => inner.Close();
        public void Dispose() => inner.Dispose();
        public bool Read() => inner.Read();
        public bool NextResult() => inner.NextResult();
        public DataTable GetSchemaTable() => inner.GetSchemaTable();
        public bool GetBoolean(int i) => inner.GetBoolean(i);
        public byte GetByte(int i) => inner.GetByte(i);
        public long GetBytes(int i, long offset, byte[] buffer, int start, int length) => inner.GetBytes(i, offset, buffer, start, length);
        public char GetChar(int i) => inner.GetChar(i);
        public long GetChars(int i, long offset, char[] buffer, int start, int length) => inner.GetChars(i, offset, buffer, start, length);
        public IDataReader GetData(int i) => inner.GetData(i);
        public string GetDataTypeName(int i) => inner.GetDataTypeName(i);
        public DateTime GetDateTime(int i) => inner.GetDateTime(i);
        public decimal GetDecimal(int i) => inner.GetDecimal(i);
        public double GetDouble(int i) => (double)GetValue(i);
        public Type GetFieldType(int i) => inner.GetFieldType(i);
        public float GetFloat(int i) => inner.GetFloat(i);
        public Guid GetGuid(int i) => inner.GetGuid(i);
        public short GetInt16(int i) => inner.GetInt16(i);
        public int GetInt32(int i) => inner.GetInt32(i);
        public long GetInt64(int i) => inner.GetInt64(i);
        public string GetName(int i) => inner.GetName(i);
        public int GetOrdinal(string name) => inner.GetOrdinal(name);
        public string GetString(int i) => inner.GetString(i);
        public int GetValues(object[] values) { int count = inner.GetValues(values); for (int i = 0; i < count; i++) values[i] = GetValue(i); return count; }
        public bool IsDBNull(int i) => inner.IsDBNull(i);
    }

    private sealed class OracleProgressObserver : IProgress<OracleExportProgress>
    {
        private readonly Action<OracleExportProgress> callback;
        public OracleProgressObserver(Action<OracleExportProgress> callback) { this.callback = callback; }
        public void Report(OracleExportProgress progress) { callback(progress); }
    }

    private sealed class MockOracleExportDatabase : IDatabase, IOracleExportReaderSource
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "oracle";
        public List<string> Commands = new List<string>();
        public string FailCommand, TextValue;
        public bool Identity, VirtualColumn, EmptyLobs, ForeignKeyCycle, MissingForeignKeyParent;
        public bool AllLargeLobRows, FloatingValues, NegativeZero, NativeView, JoinedNativeView, DisabledForeignKey, AddParent, CrossOwnerForeignKey;
        public int GeometryBytes = 21;
        public object GeometrySrid = 3826;
        public bool NullMetadataDimensions;
        public Action BeforeReaderOpen;
        public IDataReader Reader;
        public IDataReader OpenExportReader(string sql, CancellationToken token)
        {
            Commands.Add(sql);
            BeforeReaderOpen?.Invoke();
            var data = new DataTable();
            if (FloatingValues)
            {
                data.Columns.Add("F", typeof(float)); data.Columns.Add("D", typeof(double));
                data.Rows.Add(1.2345678f, NegativeZero ? BitConverter.Int64BitsToDouble(long.MinValue) : 1.2345678901234567d);
                data.Rows.Add(1.7e30f, 1.7e308d);
                Reader = NegativeZero ? (IDataReader)new OracleSignedZeroReader(data.CreateDataReader()) : data.CreateDataReader(); return Reader;
            }
            data.Columns.Add("ID", typeof(int)); data.Columns.Add("NAME", typeof(char[])); data.Columns.Add("SHAPE", typeof(byte[]));
            data.Columns.Add("SRID", typeof(int)); data.Columns.Add("GTYPE", typeof(int));
            if (EmptyLobs)
            {
                data.Columns.Add("BINARY", typeof(byte[]));
                data.Rows.Add(1, new char[0], new byte[GeometryBytes], GeometrySrid ?? DBNull.Value, 3001, new byte[0]);
                data.Rows.Add(2, DBNull.Value, new byte[21], DBNull.Value, 2001, DBNull.Value);
            }
            else
            {
                data.Rows.Add(1, (TextValue ?? "宗地一").ToCharArray(), new byte[GeometryBytes], GeometrySrid ?? DBNull.Value, 3001);
                data.Rows.Add(2, (AllLargeLobRows ? TextValue : "宗地二").ToCharArray(), new byte[AllLargeLobRows ? GeometryBytes : 21], DBNull.Value, 2001);
            }
            Reader = data.CreateDataReader();
            return Reader;
        }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            Commands.Add(sql);
            var dt = new DataTable();
            if (sql.Contains("FROM ALL_CONSTRAINTS c JOIN"))
            {
                dt.Columns.Add("CHILD_TABLE"); dt.Columns.Add("PARENT_OWNER"); dt.Columns.Add("PARENT_TABLE");
                if (DisabledForeignKey && sql.Contains("c.STATUS='ENABLED'")) return dt;
                if (ForeignKeyCycle || MissingForeignKeyParent || DisabledForeignKey) dt.Rows.Add("PARCELS", CrossOwnerForeignKey ? "A" : "SCOTT", ForeignKeyCycle ? "PARCELS" : MissingForeignKeyParent ? "UNSELECTED_PARENT" : "PARENT");
                return dt;
            }
            if (sql.Contains("TABLE(m.DIMINFO)"))
            {
                dt.Columns.Add("DIM_ORD", typeof(int)); dt.Columns.Add("DIM_NAME"); dt.Columns.Add("LOWER_BOUND", typeof(decimal)); dt.Columns.Add("UPPER_BOUND", typeof(decimal)); dt.Columns.Add("TOLERANCE", typeof(decimal));
                dt.Rows.Add(1, "E", 0m, 1000000m, 0.01m); dt.Rows.Add(2, "N", 0m, 4000000m, 0.02m); dt.Rows.Add(3, "Z", -100m, 9000m, 0.1m); return dt;
            }
            if (sql.Contains("FROM ALL_SDO_GEOM_METADATA"))
            {
                dt.Columns.Add("SRID", typeof(int)); dt.Columns.Add("DIMINFO_NULL", typeof(int)); dt.Columns.Add("DIM_COUNT", typeof(int));
                dt.Rows.Add(GeometrySrid ?? DBNull.Value, NullMetadataDimensions ? 1 : 0, NullMetadataDimensions ? (object)DBNull.Value : 3); return dt;
            }
            if (sql.Contains("FROM ALL_TABLES"))
            {
                dt.Columns.Add("OWNER", typeof(string));
                dt.Columns.Add("TABLE_NAME", typeof(string));
                dt.Columns.Add("ROW_COUNT", typeof(long));
                dt.Columns.Add("ESTIMATED_BYTES", typeof(long));

                dt.Rows.Add("SCOTT", "PARCELS", 2L, 65536L);
                dt.Rows.Add("SCOTT", "MDRT_12345$", 50L, 16384L);
                if (AddParent) dt.Rows.Add("SCOTT", "PARENT", 0L, 0L);
                return dt;
            }

            if (sql.Contains("FROM ALL_VIEWS"))
            {
                dt.Columns.Add("OWNER", typeof(string));
                dt.Columns.Add("VIEW_NAME", typeof(string));
                if (NativeView) dt.Rows.Add("SCOTT", "V");
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
                throw new InvalidOperationException("Full-table DataTable must never be used for rows.");

            }

            return dt;
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            Commands.Add(sql);
            if (FailCommand != null && sql.Contains(FailCommand)) return new Dictionary<string, string> { { "status", "NO" }, { "reason", "Simulated source command failure" } };
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
            if (FloatingValues) { dt.Columns.Add("COLUMN_NAME"); dt.Columns.Add("DATA_TYPE"); dt.Rows.Add("F", "BINARY_FLOAT"); dt.Rows.Add("D", "BINARY_DOUBLE"); return dt; }
            dt.Columns.Add("COLUMN_NAME", typeof(string));
            dt.Columns.Add("DATA_TYPE", typeof(string));
            dt.Columns.Add("VIRTUAL_COLUMN"); dt.Columns.Add("IDENTITY_COLUMN"); dt.Columns.Add("IDENTITY_GENERATION");
            dt.Rows.Add("ID", "NUMBER");
            dt.Rows.Add("NAME", TextValue == null && !EmptyLobs ? "VARCHAR2" : "CLOB");
            dt.Rows.Add("SHAPE", "SDO_GEOMETRY");
            if (Identity) { dt.Rows[0]["IDENTITY_COLUMN"] = "YES"; dt.Rows[0]["IDENTITY_GENERATION"] = "ALWAYS"; }
            if (VirtualColumn) dt.Rows.Add("CALC", "NUMBER", "YES");
            if (EmptyLobs) dt.Rows.Add("BINARY", "BLOB");
            return dt;
        }
        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public DataTable GetTableStatus(string databaseName) => new DataTable();
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();
        public string GetTableCreateStatement(string databaseName, string tableName)
        {
            if (tableName == "PARENT") return "CREATE TABLE \"SCOTT\".\"PARENT\" (\"ID\" NUMBER PRIMARY KEY);";
            if (DisabledForeignKey) return "CREATE TABLE \"SCOTT\".\"PARCELS\" (\"ID\" NUMBER, CONSTRAINT \"FK_DISABLED\" FOREIGN KEY (\"ID\") REFERENCES \"SCOTT\".\"PARENT\"(\"ID\") DISABLE);";
            return "CREATE TABLE \"SCOTT\".\"PARCELS\" (\"ID\" NUMBER, \"NAME\" VARCHAR2(50), \"SHAPE\" MDSYS.SDO_GEOMETRY); COMMENT ON TABLE \"SCOTT\".\"PARCELS\" IS 'actual comment'; CREATE INDEX \"SCOTT\".\"IX_NAME\" ON \"SCOTT\".\"PARCELS\" (\"NAME\"); CREATE INDEX \"SCOTT\".\"REAL_SPATIAL_INDEX\" ON \"SCOTT\".\"PARCELS\" (\"SHAPE\") INDEXTYPE IS MDSYS.SPATIAL_INDEX;";
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
        public string GetViewCreateStatement(string databaseName, string viewName) => NativeView ? (JoinedNativeView ? "CREATE OR REPLACE FORCE EDITIONABLE VIEW \"SCOTT\".\"V\" AS SELECT a.\"ID\" FROM \"SCOTT\".\"PARCELS\" a JOIN \"SCOTT\".\"PARCELS\" b ON a.\"ID\"=b.\"ID\";" : "CREATE OR REPLACE FORCE EDITIONABLE VIEW \"SCOTT\".\"V\" AS SELECT \"ID\" FROM \"SCOTT\".\"PARCELS\";") : "";
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { }
    }
}
