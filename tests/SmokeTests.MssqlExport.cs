using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using mySQLPunk.lib;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
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
