using System;
using System.Data.SqlClient;
using System.Threading;
namespace mySQLPunk.lib.MssqlExport
{
    // Borrows the caller's live connection/transaction. Never owns or opens a session.
    internal sealed class MssqlSnapshotBackend
    {
        private readonly SqlConnection connection;
        private readonly SqlTransaction transaction;
        internal MssqlSnapshotBackend(SqlConnection connection, SqlTransaction transaction = null)
        { this.connection = connection ?? throw new ArgumentNullException(nameof(connection)); this.transaction = transaction; }
        internal MssqlSnapshotCapability ReadCapability(CancellationToken cancellation)
        {
            const string sql = @"SELECT CONVERT(int,snapshot_isolation_state) snapshot_state,
CONVERT(int,database_id) database_id,CONVERT(datetime,create_date) create_date,
CONVERT(nvarchar(128),name) database_name,CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) server_name,
CONVERT(int,HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER')) can_alter,
CONVERT(nvarchar(60),DATABASEPROPERTYEX(DB_NAME(),'Updateability')) updateability
FROM sys.databases WHERE database_id=DB_ID()";
            var rows = MssqlExportMetadataReader.Query(connection, transaction, sql, cancellation);
            if (rows.Count != 1) throw new MssqlExportPrerequisiteException(MssqlPrerequisite.DatabaseUnavailable);
            var row = rows[0];
            var identity = new MssqlSnapshotIdentity((string)row["server_name"], (string)row["database_name"],
                row["database_id"] == null ? 0 : (int)row["database_id"], row["create_date"] == null ? default(DateTime) : (DateTime)row["create_date"]);
            string updateability = (string)row["updateability"];
            bool? writable = updateability == "READ_WRITE" ? true : updateability == "READ_ONLY" ? false : (bool?)null;
            return new MssqlSnapshotCapability(identity, row["snapshot_state"] == null ? -1 : (int)row["snapshot_state"],
                row["can_alter"] == null ? (bool?)null : (int)row["can_alter"] == 1, writable);
        }
    }
}
