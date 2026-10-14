using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using mySQLPunk.lib;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
    // A geometry name alone must never exempt an alias/user CLR type from preflight.
    private static void Metadata_Geometry()
    {
        foreach (var mode in new[] { MssqlExportMode.SchemaAndData, MssqlExportMode.DataOnly, MssqlExportMode.SchemaOnly })
        foreach (var fact in new[] { "native", "user-schema", "user-defined", "geography" }) {
            var c = new MssqlColumnDefinition(new Dictionary<string, object> {
                { "column_id", 1 }, { "name", "g" }, { "type_name", fact == "geography" ? "geography" : "geometry" },
                { "type_schema", fact == "user-schema" ? "dbo" : "sys" }, { "is_user_defined", fact == "user-defined" }, { "is_assembly_type", true } });
            var t = ValueTable(c);
            var request = new MssqlExportRequest(Guid.NewGuid(), "fixture", new[] { t.Key }, mode, false, Path.Combine(Path.GetTempPath(), "geometry.sql"), null, null, mode != MssqlExportMode.SchemaOnly);
            var issues = new MssqlExportMetadataReader().Validate(new MssqlExportSchema(new[] { t }, null, null, 1, null), request);
            Assert((issues.Count == 0) == (fact == "native"), "Only native sys.geometry accepted in " + mode + ": " + fact);
            if (fact != "native") Assert(issues.Any(x => x.ColumnName == "g"), "Rejected type identifies column");
        }
    }

    private static void Diagnostic_MetadataModes()
    {
        foreach (var mode in new[] { MssqlExportMode.SchemaOnly, MssqlExportMode.SchemaAndData, MssqlExportMode.DataOnly })
        foreach (int state in new[] { 0, 1, 2, 3, 9 })
        foreach (bool? select in new bool?[] { false, true, null }) {
            var table = new MssqlTableDefinition(new MssqlObjectKey(1, "dbo", "visible"), new Dictionary<string, object> { { "can_select", select.HasValue ? (object)select.Value : DBNull.Value } }, null, null, null);
            var request = new MssqlExportRequest(Guid.NewGuid(), "fixture", new[] { table.Key }, mode, false, Path.Combine(Path.GetTempPath(), "matrix.sql"), null, null, mode != MssqlExportMode.SchemaOnly);
            var identity = new MssqlSnapshotIdentity("server", "fixture", 1, new DateTime(2026, 1, 1));
            var schema = new MssqlExportSchema(new[] { table }, null, null, state, null, null, new MssqlSnapshotCapability(identity, state, true, true));
            var issues = new MssqlExportMetadataReader().ValidateStart(schema, request);
            Assert(issues.Any(x => x.Code == "UnreadableTable") == (select != true && mode != MssqlExportMode.SchemaOnly), "Schema-only needs readable definitions, not SELECT; both data modes need SELECT");
            Assert(issues.Any(x => x.Code == "SnapshotUnavailable") == (state != 0 && state != 1 && mode != MssqlExportMode.SchemaOnly), "Snapshot state gates only data modes, including unknown states");
            Assert(new MssqlExportPreflight(schema, issues).CanStart == (mode == MssqlExportMode.SchemaOnly || (select == true && (state == 0 || state == 1))), "Mode/state/permission matrix cannot silently pass missing data prerequisites");
        }
    }
    private static void Metadata_RequiresNativeReader()
    {
        Assert(typeof(MssqlObjectKey).Assembly.GetType("mySQLPunk.lib.MssqlExport.MssqlExportMetadataReader") != null, "Native metadata reader must exist");
    }

    private static void Metadata_MapsNativeFactsAndValidates()
    {
        var a = new MssqlObjectKey(101, "dbo", "ord]ers");
        var b = new MssqlObjectKey(102, "sales", "ord]ers");
        var shape = new MssqlColumnDefinition(new Dictionary<string, object> { { "column_id", 1 }, { "name", "shape" }, { "type_name", "geometry" }, { "xml_collection_id", 0 } });
        var xml = new MssqlColumnDefinition(new Dictionary<string, object> { { "column_id", 2 }, { "name", "xml" }, { "type_name", "xml" }, { "xml_collection_id", 9 } });
        var udt = new MssqlColumnDefinition(new Dictionary<string, object> { { "column_id", 3 }, { "name", "custom" }, { "type_name", "MyType" }, { "is_user_defined", true } });
        var tables = new[] { new MssqlTableDefinition(a, new Dictionary<string, object>(), new[] { shape, xml, udt }, null, null), new MssqlTableDefinition(b, new Dictionary<string, object>(), null, null, null) };
        var catalog = new MssqlExportCatalog(tables, 0, null);
        Assert(catalog.Tables.Count == 2, "dbo and sales objects remain distinct");
        Assert(catalog.Tables[0].EstimatedRows == null && catalog.Tables[0].EstimatedBytes == null, "Missing estimates remain unknown");
        Assert(MssqlExportMetadataReader.Quote(a.Name) == "[ord]]ers]", "Quote each identifier segment");
        var request = new MssqlExportRequest(Guid.NewGuid(), "fixture", new[] { a, b }, MssqlExportMode.SchemaAndData, false, Path.Combine(Path.GetTempPath(), "fixture.sql"), null, null, true);
        var schema = new MssqlExportSchema(tables, null, new[] { new MssqlDependency(a.ObjectId, 999, null, null, "dbo", "missing") }, 0, null);
        var issues = new MssqlExportMetadataReader().Validate(schema, request);
        Assert(System.Linq.Enumerable.Any(issues, x => x.Code == "UnsupportedSpatial" && x.ColumnName == "shape"), "Spatial issue names column");
        Assert(System.Linq.Enumerable.Any(issues, x => x.Code == "UnsupportedTypedXml"), "Typed XML rejected");
        Assert(System.Linq.Enumerable.Any(issues, x => x.Code == "UnsupportedUdt"), "UDT rejected");
        Assert(System.Linq.Enumerable.Any(issues, x => x.Code == "MissingDependency"), "Dependencies must be selected");
        for (int state = 0; state <= 3; state++)
        {
            var candidate = new MssqlExportSchema(tables, null, null, state, null);
            Assert(System.Linq.Enumerable.Any(new MssqlExportMetadataReader().Validate(candidate, request), x => x.Code == "SnapshotUnavailable") == false, "Final shape validation never repeats settings capability gate");
        }
        var v1 = new MssqlViewDefinition(new MssqlObjectKey(201, "dbo", "v1"), new Dictionary<string, object> { { "definition", "CREATE VIEW dbo.v1 AS SELECT * FROM dbo.v2" } });
        var v2 = new MssqlViewDefinition(new MssqlObjectKey(202, "dbo", "v2"), new Dictionary<string, object> { { "definition", "CREATE VIEW dbo.v2 AS SELECT * FROM dbo.v1" } });
        var cycle = new MssqlExportSchema(tables, new[] { v1, v2 }, new[] { new MssqlDependency(201, 202, null, null, "dbo", "v2"), new MssqlDependency(202, 201, null, null, "dbo", "v1") }, 1, null);
        Assert(System.Linq.Enumerable.Any(new MssqlExportMetadataReader().Validate(cycle, request), x => x.Code == "DependencyCycle"), "Cyclic view definitions cannot be ordered for restore");
        var duplicate = new MssqlExportSchema(new[] { tables[0], tables[0] }, null, null, 1, null);
        Assert(System.Linq.Enumerable.Any(new MssqlExportMetadataReader().Validate(duplicate, request), x => x.Code == "DuplicateSelection"), "Repeated table selection cannot duplicate output");
        var changed = new MssqlExportSchema(new[] { new MssqlTableDefinition(a, new Dictionary<string, object> { { "comment", "changed" } }, new[] { shape, xml, udt }, null, null), tables[1] }, null, schema.Dependencies, 0, null);
        Assert(schema.Fingerprint != changed.Fingerprint, "Definition fingerprint includes comments");
        var estimated = new MssqlExportSchema(new[] { new MssqlTableDefinition(a, new Dictionary<string, object> { { "estimated_rows", 123L } }, new[] { shape, xml, udt }, null, null), tables[1] }, null, schema.Dependencies, 0, null);
        Assert(schema.Fingerprint == estimated.Fingerprint, "Changing row estimate is not schema drift");
    }

    private static void Metadata_StorageAndCapability()
    {
        var rows = new[] {
            new Dictionary<string, object> { { "object_id", 1 }, { "schema_name", "dbo" }, { "name", "wide" }, { "estimated_rows", 100L } },
            new Dictionary<string, object> { { "object_id", 2 }, { "schema_name", "sales" }, { "name", "narrow" }, { "estimated_rows", 100L } },
            new Dictionary<string, object> { { "object_id", 3 }, { "schema_name", "dbo" }, { "name", "unavailable" }, { "estimated_rows", 100L } }
        };
        var storage = new[] {
            new Dictionary<string, object> { { "object_id", 1 }, { "estimated_bytes", 819200L } },
            new Dictionary<string, object> { { "object_id", 2 }, { "estimated_bytes", 8192L } }
        };
        var tables = MssqlExportMetadataReader.MapTables(rows, storage);
        Assert(tables[0].EstimatedBytes == 819200 && tables[1].EstimatedBytes == 8192 && tables[2].EstimatedBytes == null, "Catalog storage estimates map by object and preserve unavailable");
        Assert(MssqlExportMetadataReader.MapTables(rows, null).All(t => !t.EstimatedBytes.HasValue), "Optional storage query failure keeps estimates unavailable");
        var schema = new MssqlExportSchema(tables.Take(2), null, null, 1, null);
        var estimator = new MssqlExportProgressEstimator(Guid.NewGuid(), schema, false);
        var progress = estimator.Sample(MssqlExportStage.Data, tables[0].Key, 10, 81920, 1000, TimeSpan.FromSeconds(3), true);
        Assert(progress.EstimatedBytes == 827392 && progress.Percent.HasValue && progress.Percent.Value < 85, "Native mapped sizes feed non-authoritative estimator");
        var unknown = new MssqlExportProgressEstimator(Guid.NewGuid(), new MssqlExportSchema(tables, null, null, 1, null), false).Sample(MssqlExportStage.Data, tables[0].Key, 10, 81920, 1000, TimeSpan.FromSeconds(3), true);
        Assert(!unknown.Percent.HasValue, "Unavailable storage keeps indeterminate data percent");
        var changedStorage = MssqlExportMetadataReader.MapTables(rows, new[] {
            new Dictionary<string, object> { { "object_id", 1 }, { "estimated_bytes", 1638400L } },
            new Dictionary<string, object> { { "object_id", 2 }, { "estimated_bytes", 16384L } }
        });
        Assert(schema.Fingerprint == new MssqlExportSchema(changedStorage.Take(2), null, null, 1, null).Fingerprint, "Mutable storage estimates cannot cause schema drift");
        AssertContains(MssqlExportMetadataReader.OptionalProjection("i", "optimize_for_sequential_key", new string[0]), "CAST(NULL AS int)", "Absent index capability remains unknown");
        var key = new MssqlObjectKey(1, "dbo", "ordinary");
        var column = new MssqlColumnDefinition(new Dictionary<string, object> { { "column_id", 1 }, { "name", "id" }, { "type_name", "int" } });
        var index = new MssqlIndexDefinition(new Dictionary<string, object> { { "index_id", 1 }, { "name", "pk" }, { "type", 1 }, { "is_primary_key", true }, { "optimize_for_sequential_key", null } }, new[] { new MssqlNativeFacts(new Dictionary<string, object> { { "index_column_id", 1 }, { "name", "id" }, { "key_ordinal", 1 } }) });
        var ddl = new StringWriter();
        var oldSchema = new MssqlExportSchema(new[] { new MssqlTableDefinition(key, new Dictionary<string, object>(), new[] { column }, new[] { index }, null) }, null, null, 1, null);
        new MssqlSchemaScriptWriter().WriteAfterData(oldSchema, new MssqlExportRequest(Guid.NewGuid(), "db", new[] { key }, MssqlExportMode.SchemaAndData, false, Path.Combine(Path.GetTempPath(), "unused.sql"), null, null, true), ddl, System.Threading.CancellationToken.None);
        AssertNotContains(ddl.ToString(), "OPTIMIZE_FOR_SEQUENTIAL_KEY", "Unavailable old-engine index option omitted from DDL");
    }

    private static void Metadata_CancelsBeforeAndDuringReaderStartup()
    {
        foreach (bool cancelBefore in new[] { true, false })
        {
            using (var cancellation = new System.Threading.CancellationTokenSource())
            {
                var command = new MetadataCommand(new List<string>(), null);
                command.CancelAtStartup = cancelBefore ? null : cancellation;
                if (cancelBefore) cancellation.Cancel();
                using (var owner = new MssqlCommandOwner(command, cancellation.Token))
                    AssertThrows<OperationCanceledException>(() => owner.ExecuteReader(CommandBehavior.SequentialAccess), "Startup cancellation must reach the provider even after idle Cancel was consumed");
            }
        }
    }

    private static void Metadata_DisposesReaderAndCancellationBeforeCommand()
    {
        var events = new List<string>();
        var cancellation = new System.Threading.CancellationTokenSource();
        var command = new MetadataCommand(events, cancellation);
        var owner = new MssqlCommandOwner(command, cancellation.Token);
        var table = new DataTable(); table.Columns.Add("id", typeof(int)); table.Rows.Add(1);
        var reader = table.CreateDataReader();
        command.Reader = reader;
        var rows = new MssqlRowReader(reader, owner);
        rows.Dispose(); rows.Dispose();
        Assert(events.Count == 1 && events[0] == "command", "Command disposed once after reader, cancel registration removed before command disposal");
        Assert(reader.IsClosed, "Row reader closes underlying reader");
        using (var cancel = new System.Threading.CancellationTokenSource())
        {
            var live = new MetadataCommand(events, null);
            using (var owned = new MssqlCommandOwner(live, cancel.Token)) cancel.Cancel();
            Assert(events.Contains("cancel"), "Owned command receives cancellation");
        }
        cancellation.Dispose();
    }
    private sealed class MetadataCommand : System.Data.Common.DbCommand
    {
        private readonly List<string> events;
        private readonly System.Threading.CancellationTokenSource cancellation;
        public System.Data.Common.DbDataReader Reader;
        public System.Threading.CancellationTokenSource CancelAtStartup;
        public MetadataCommand(List<string> events, System.Threading.CancellationTokenSource cancellation) { this.events = events; this.cancellation = cancellation; }
        public override void Cancel() { events.Add("cancel"); }
        protected override void Dispose(bool disposing) { if (disposing) { Assert(Reader == null || Reader.IsClosed, "Reader must close before command"); cancellation?.Cancel(); events.Add("command"); } base.Dispose(disposing); }
        public override string CommandText { get; set; }
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override System.Data.Common.DbConnection DbConnection { get; set; }
        protected override System.Data.Common.DbTransaction DbTransaction { get; set; }
        protected override System.Data.Common.DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        public override int ExecuteNonQuery() { throw new NotSupportedException(); }
        public override object ExecuteScalar() { throw new NotSupportedException(); }
        public override void Prepare() { throw new NotSupportedException(); }
        protected override System.Data.Common.DbParameter CreateDbParameter() { throw new NotSupportedException(); }
        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { return new DataTable().CreateDataReader(); }
        protected override System.Threading.Tasks.Task<System.Data.Common.DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, System.Threading.CancellationToken token)
        {
            // Cancel() while idle intentionally has no effect on a later execution.
            // A provider that receives the token can reject cancellation during startup.
            CancelAtStartup?.Cancel();
            token.ThrowIfCancellationRequested();
            return System.Threading.Tasks.Task.FromResult<System.Data.Common.DbDataReader>(new DataTable().CreateDataReader());
        }
    }

    private static void Session_DisposesOwnerOnce()
    {
        MigrationSession_FactoryOwnership();
        var owner = new MssqlSessionOwner();
        using (var connection = new SqlConnection())
        {
            bool connectionDisposed = false;
            connection.Disposed += (sender, args) => connectionDisposed = true;
            var result = new ConnectionOpenResult(owner, null, "test", connection);
            Assert(ReferenceEquals(owner, result.Database) && ReferenceEquals(connection, result.NativeMssqlConnection), "Open result must retain the same owner and native connection");
            var session = new MssqlOwnedSession(owner, connection);
            Assert(ReferenceEquals(connection, session.Connection), "Must retain original provider connection");
            session.Dispose(); session.Dispose();
            Assert(owner.DisposeCount == 1, "SQL provider and SSH lease have one owner");
            Assert(!connectionDisposed, "Session must not independently dispose the owner connection");
        }
        AssertThrows<ArgumentNullException>(() => new MssqlOwnedSession(null, new SqlConnection()), "Null owner rejected");
        AssertThrows<ArgumentNullException>(() => new MssqlOwnedSession(owner, null), "Null connection rejected");
        AssertThrows<ArgumentException>(() => ConnectionOpenService.OpenMssqlSession(new Dictionary<string, object> { { "db_kind", "sqlite" } }), "Other provider rejected before connection");
    }

    private static void MigrationSession_FactoryOwnership()
    {
        var settings = new Dictionary<string, object> {
            { "db_kind", "mssql" }, { "pwd", "synthetic-fixture-only" }, { "ssh_enabled", true },
            { "initial_database", "original" }, { "connString", "must-not-be-used" }, { "pdo", new object() }
        };
        foreach (string selected in new[] { "source_fixture", "target_fixture" })
        {
            var provider = new MssqlSessionOwner();
            using (var connection = new SqlConnection())
            {
                var owned = new MssqlOwnedSession(provider, connection);
                using (var migration = new SqlServerMigrationSession(settings, selected, copied => {
                    Assert((string)copied["pwd"] == "synthetic-fixture-only", "SQL auth uses runtime settings, not redacted ConnectionString");
                    Assert((bool)copied["ssh_enabled"] && (string)copied["initial_database"] == selected, "Selected endpoint and SSH settings reach factory");
                    Assert(!copied.ContainsKey("pdo") && !copied.ContainsKey("connString"), "Live provider and cached connection string excluded");
                    return owned;
                }))
                {
                    Assert(provider.DisposeCount == 0, "Provider/SSH owner retained while migration runs");
                    migration.Dispose();
                }
                Assert(provider.DisposeCount == 1, "Migration disposes provider/SSH owner once");
            }
            AssertThrows<InvalidOperationException>(() => new SqlServerMigrationSession(settings, selected, copied => {
                throw new InvalidOperationException("synthetic factory failure");
            }), "Both endpoints propagate factory failures");
        }
        var sourceOwner = new MssqlSessionOwner();
        using (var connection = new SqlConnection())
        {
            AssertThrows<InvalidOperationException>(() => {
                using (var source = new SqlServerMigrationSession(settings, "source_fixture", copied => new MssqlOwnedSession(sourceOwner, connection)))
                using (var target = new SqlServerMigrationSession(settings, "target_fixture", copied => { throw new InvalidOperationException(); })) { }
            }, "Target open failure unwinds source ownership");
        }
        Assert(sourceOwner.DisposeCount == 1, "Source owner released when target factory fails");
        Assert((string)settings["initial_database"] == "original", "Factory does not mutate stored endpoint settings");
    }

    private static void Request_CopiesSelectionAndRejectsInvalidMode()
    {
        var tables = new List<MssqlObjectKey> { new MssqlObjectKey(7, "dbo", "Orders") };
        string path = Path.Combine(Path.GetTempPath(), "mssql-export.sql");
        var request = new MssqlExportRequest(Guid.NewGuid(), "shop", tables, MssqlExportMode.SchemaAndData, true, path, null, null, true);
        tables.Clear();
        Assert(request.Tables.Count == 1, "Request must not retain mutable selection");
        AssertThrows<NotSupportedException>(() => ((IList<MssqlObjectKey>)request.Tables).Clear(), "Selection is read-only");
        AssertThrows<ArgumentException>(() => new MssqlExportRequest(Guid.NewGuid(), "shop", request.Tables, (MssqlExportMode)99, false, path, null, null), "Unknown mode rejected");
        AssertThrows<ArgumentException>(() => new MssqlExportRequest(Guid.NewGuid(), "shop", request.Tables, MssqlExportMode.DataOnly, true, path, null, null, true), "Data-only cannot include views");
        AssertThrows<ArgumentException>(() => new MssqlExportRequest(Guid.NewGuid(), "shop", tables, MssqlExportMode.SchemaOnly, false, path, null, null), "Empty selection rejected");
        AssertThrows<ArgumentException>(() => new MssqlExportRequest(Guid.NewGuid(), "shop", request.Tables, MssqlExportMode.SchemaOnly, false, " ", null, null), "Empty path rejected");
        Assert(MssqlExportLimits.SmallRowBytes == 262144, "Row buffer budget");
        Assert(MssqlExportLimits.StatementBytes == 262144 && MssqlExportLimits.BatchBytes == 4194304 && MssqlExportLimits.BinaryChunkBytes == 65536 && MssqlExportLimits.TextChunkCodeUnits == 16384, "Bounded streaming budgets");
    }
    private sealed class MssqlSessionOwner : IDatabase
    {
        public int DisposeCount;
        public ConnectionState State => ConnectionState.Closed;
        public string ProviderName => "mssql";
        public void Dispose() { DisposeCount++; }
        public void SetConn(string connectionString) { throw new NotSupportedException(); }
        public void Open() { throw new NotSupportedException(); }
        public void Close() { throw new NotSupportedException(); }
        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public System.Threading.Tasks.Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public System.Threading.Tasks.Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public List<string> GetDatabases() { throw new NotSupportedException(); }
        public List<string> GetTables(string databaseName) { throw new NotSupportedException(); }
        public List<string> GetViews(string databaseName) { throw new NotSupportedException(); }
        public DataTable GetColumns(string databaseName, string tableName) { throw new NotSupportedException(); }
        public DataTable GetIndexes(string databaseName, string tableName) { throw new NotSupportedException(); }
        public DataTable GetTableStatus(string databaseName) { throw new NotSupportedException(); }
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) { throw new NotSupportedException(); }
        public string GetTableCreateStatement(string databaseName, string tableName) { throw new NotSupportedException(); }
        public bool TableExists(string databaseName, string tableName) { throw new NotSupportedException(); }
        public bool ViewExists(string databaseName, string viewName) { throw new NotSupportedException(); }
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { throw new NotSupportedException(); }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { throw new NotSupportedException(); }
        public long CountRows(string databaseName, string tableName) { throw new NotSupportedException(); }
        public DataTable GetCopyColumns(string databaseName, string tableName) { throw new NotSupportedException(); }
        public DataTable GetCopyIndexes(string databaseName, string tableName) { throw new NotSupportedException(); }
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { throw new NotSupportedException(); }
        public void DropTableForCopy(string databaseName, string tableName) { throw new NotSupportedException(); }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { throw new NotSupportedException(); }
        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit) { throw new NotSupportedException(); }
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { throw new NotSupportedException(); }
        public string GetViewCreateStatement(string databaseName, string viewName) { throw new NotSupportedException(); }
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { throw new NotSupportedException(); }
    }
}
