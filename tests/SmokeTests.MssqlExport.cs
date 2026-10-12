using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using mySQLPunk.lib;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
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
        var request = new MssqlExportRequest(Guid.NewGuid(), "fixture", new[] { a, b }, MssqlExportMode.SchemaAndData, false, Path.Combine(Path.GetTempPath(), "fixture.sql"), null, null);
        var schema = new MssqlExportSchema(tables, null, new[] { new MssqlDependency(a.ObjectId, 999, null, null, "dbo", "missing") }, 0, null);
        var issues = new MssqlExportMetadataReader().Validate(schema, request);
        Assert(System.Linq.Enumerable.Any(issues, x => x.Code == "UnsupportedSpatial" && x.ColumnName == "shape"), "Spatial issue names column");
        Assert(System.Linq.Enumerable.Any(issues, x => x.Code == "UnsupportedTypedXml"), "Typed XML rejected");
        Assert(System.Linq.Enumerable.Any(issues, x => x.Code == "UnsupportedUdt"), "UDT rejected");
        Assert(System.Linq.Enumerable.Any(issues, x => x.Code == "MissingDependency"), "Dependencies must be selected");
        for (int state = 0; state <= 3; state++)
        {
            var candidate = new MssqlExportSchema(tables, null, null, state, null);
            Assert(System.Linq.Enumerable.Any(new MssqlExportMetadataReader().Validate(candidate, request), x => x.Code == "SnapshotUnavailable") == (state != 1), "Only ON accepts data snapshot");
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

    private static void Request_CopiesSelectionAndRejectsInvalidMode()
    {
        var tables = new List<MssqlObjectKey> { new MssqlObjectKey(7, "dbo", "Orders") };
        string path = Path.Combine(Path.GetTempPath(), "mssql-export.sql");
        var request = new MssqlExportRequest(Guid.NewGuid(), "shop", tables, MssqlExportMode.SchemaAndData, true, path, null, null);
        tables.Clear();
        Assert(request.Tables.Count == 1, "Request must not retain mutable selection");
        AssertThrows<NotSupportedException>(() => ((IList<MssqlObjectKey>)request.Tables).Clear(), "Selection is read-only");
        AssertThrows<ArgumentException>(() => new MssqlExportRequest(Guid.NewGuid(), "shop", request.Tables, (MssqlExportMode)99, false, path, null, null), "Unknown mode rejected");
        AssertThrows<ArgumentException>(() => new MssqlExportRequest(Guid.NewGuid(), "shop", request.Tables, MssqlExportMode.DataOnly, true, path, null, null), "Data-only cannot include views");
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
