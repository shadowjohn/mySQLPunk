using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Globalization;
using System.Threading;
using mySQLPunk.lib.MssqlExport;

namespace mySQLPunk.lib
{
    public sealed class SqlServerMigrationSession : ISqlServerMigrationSession
    {
        private readonly SqlConnection connection;
        private readonly MssqlOwnedSession owner;
        private SqlTransaction transaction;

        internal SqlServerMigrationSession(Dictionary<string, object> settings, string database,
            Func<Dictionary<string, object>, MssqlOwnedSession> factory = null)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            var selected = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in settings)
            {
                if (string.Equals(pair.Key, "pdo", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pair.Key, "connString", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pair.Key, "isConnect", StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Value == null || pair.Value is string || pair.Value.GetType().IsPrimitive || pair.Value is decimal)
                    selected[pair.Key] = pair.Value;
            }
            selected["db_kind"] = "mssql";
            selected["provider"] = "mssql";
            selected["initial_database"] = database;
            owner = (factory ?? ConnectionOpenService.OpenMssqlImportSession)(selected);
            if (owner == null) throw new InvalidOperationException("連線工廠未回傳 SQL Server session。");
            connection = owner.Connection;
        }

        private DataTable Query(string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = sql;
                using (var reader = command.ExecuteReader())
                {
                    var table = new DataTable(); table.Load(reader); return table;
                }
            }
        }

        public SqlServerMigrationCatalog ReadCatalog()
        {
            return SqlServerMigrationCatalogReader.Read(Query);
        }

        public void Begin(bool source)
        {
            transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            if (!source) Execute("SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET ANSI_PADDING ON;");
            if (!source) Execute("DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=N'mySQLPunk:compatibility-migration', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=0; IF @result<0 THROW 50000, 'Migration busy', 1;");
        }

        public IEnumerable<DataTable> ReadBatches(SqlServerMigrationTable table, int batchSize, CancellationToken cancellationToken)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT " + string.Join(",", table.Columns.ConvertAll(column => SqlServerCompatibilityMigration.Quote(column.Name))) + " FROM [dbo]." + SqlServerCompatibilityMigration.Quote(table.Name);
                using (cancellationToken.Register(command.Cancel))
                using (var reader = command.ExecuteReader())
                {
                    var batch = new DataTable();
                    for (int column = 0; column < reader.FieldCount; column++) batch.Columns.Add(reader.GetName(column), reader.GetFieldType(column));
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var values = new object[reader.FieldCount]; reader.GetValues(values); batch.Rows.Add(values);
                        if (batch.Rows.Count == batchSize) { yield return batch; batch = batch.Clone(); }
                    }
                    if (batch.Rows.Count > 0) yield return batch;
                }
            }
        }

        public void Execute(string sql)
        {
            using (var command = connection.CreateCommand()) { command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery(); }
        }
        public void Insert(SqlServerMigrationTable table, DataTable rows)
        {
            SqlServerBatchInsert.Execute(connection, "[dbo]." + SqlServerCompatibilityMigration.Quote(table.Name), rows, transaction);
        }
        public void Commit() { transaction.Commit(); transaction.Dispose(); transaction = null; }
        public void Rollback() { if (transaction != null) { transaction.Rollback(); transaction.Dispose(); transaction = null; } }
        public void Dispose()
        {
            try { if (transaction != null) transaction.Dispose(); }
            finally { owner.Dispose(); }
        }
    }
}
