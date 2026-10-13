using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
namespace mySQLPunk.lib.MssqlExport
{
    // A positive statement that the setting command was NOT sent; other errors remain uncertain.
    internal sealed class MssqlSnapshotSettingNotAttemptedException : InvalidOperationException
    { internal MssqlSnapshotSettingNotAttemptedException() : base("SnapshotSettingNotAttempted") { } }
    // Borrows the caller's live connection/transaction. Never owns or opens a session.
    internal sealed class MssqlSnapshotBackend : IMssqlSnapshotBackend, IMssqlSnapshotDeadlineBackend
    {
        internal const string LockResource = "mySQLPunk.MssqlExport.SnapshotSetting.v1";
        private readonly SqlConnection connection;
        private readonly SqlTransaction transaction;
        private bool lockHeld;
        private Guid sessionId;
        private MssqlSnapshotIdentity lockedIdentity;
        private IMssqlSnapshotDeadline deadline;
        IMssqlSnapshotDeadline IMssqlSnapshotDeadlineBackend.Deadline { set { deadline = value; } }
        public bool IsUsable => connection.State == ConnectionState.Open && (!lockHeld || connection.ClientConnectionId == sessionId);
        internal MssqlSnapshotBackend(SqlConnection connection, SqlTransaction transaction = null)
        { this.connection = connection ?? throw new ArgumentNullException(nameof(connection)); this.transaction = transaction; }
        public MssqlSnapshotCapability ReadCapability(CancellationToken cancellation)
        {
            const string sql = @"SELECT CONVERT(int,snapshot_isolation_state) snapshot_state,
CONVERT(int,database_id) database_id,CONVERT(datetime,create_date) create_date,
CONVERT(nvarchar(128),name) database_name,CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) server_name,
CONVERT(int,HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER')) can_alter,
CONVERT(nvarchar(60),DATABASEPROPERTYEX(DB_NAME(),'Updateability')) updateability,
APPLOCK_MODE('public','mySQLPunk.MssqlExport.SnapshotSetting.v1','Session') lock_mode
FROM sys.databases WHERE database_id=DB_ID()";
            using (var command = Command(sql, cancellation))
            using (var reader = command.ExecuteReaderAsync(cancellation).GetAwaiter().GetResult())
            {
                if (!reader.ReadAsync(cancellation).GetAwaiter().GetResult()) throw new MssqlExportPrerequisiteException(MssqlPrerequisite.DatabaseUnavailable);
                Func<string, object> value = name => reader[name] == DBNull.Value ? null : reader[name];
                if (lockHeld && !string.Equals((string)value("lock_mode"), "Exclusive", StringComparison.Ordinal)) throw new InvalidOperationException("SnapshotLockLost");
                var identity = new MssqlSnapshotIdentity((string)value("server_name"), (string)value("database_name"),
                    value("database_id") == null ? 0 : (int)value("database_id"), value("create_date") == null ? default(DateTime) : (DateTime)value("create_date"));
                string updateability = (string)value("updateability");
                bool? writable = updateability == "READ_WRITE" ? true : updateability == "READ_ONLY" ? false : (bool?)null;
                var capability = new MssqlSnapshotCapability(identity, value("snapshot_state") == null ? -1 : (int)value("snapshot_state"),
                    value("can_alter") == null ? (bool?)null : (int)value("can_alter") == 1, writable);
                if (lockHeld && lockedIdentity == null) lockedIdentity = identity;
                return capability;
            }
        }
        public bool TryAcquireLock(CancellationToken cancellation)
        {
            if (lockHeld || transaction != null) throw new InvalidOperationException("SnapshotLockInvalidContext");
            using (var command = Command(@"DECLARE @result int;
EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@DbPrincipal='public',@LockTimeout=0;
SELECT @result;", cancellation))
            {
                command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
                int code = Convert.ToInt32(command.ExecuteScalarAsync(cancellation).GetAwaiter().GetResult());
                if (code == -1) return false;
                if (code == -2) throw new OperationCanceledException("SnapshotLockCanceled", cancellation);
                if (code < 0) throw new InvalidOperationException(code == -3 ? "SnapshotLockDeadlock" : "SnapshotLockFailed");
                lockHeld = true; sessionId = connection.ClientConnectionId; return true;
            }
        }
        public void SetEnabled(bool enabled, CancellationToken cancellation)
        {
            if (!lockHeld || transaction != null || !IsUsable) throw new MssqlSnapshotSettingNotAttemptedException();
            var fresh = ReadCapability(cancellation);
            if (!MssqlSnapshotLifecycle.SameIdentity(lockedIdentity, fresh.Identity)) throw new MssqlSnapshotSettingNotAttemptedException();
            if (!fresh.CanManage || fresh.State != (enabled ? 0 : 1)) throw new MssqlSnapshotSettingNotAttemptedException();
            // Guard in the SAME batch as ALTER: transparent provider reconnect must not authorize an unlocked session.
            using (var command = Command(@"IF ISNULL(APPLOCK_MODE('public',@resource,'Session'),'NoLock') <> 'Exclusive'
 OR ISNULL(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),'') COLLATE Latin1_General_100_BIN2 <> @server COLLATE Latin1_General_100_BIN2
 OR NOT EXISTS(SELECT 1 FROM sys.databases WHERE database_id=DB_ID() AND database_id=@id
 AND name COLLATE Latin1_General_100_BIN2=@name COLLATE Latin1_General_100_BIN2 AND create_date=@created)
 THROW 50000,'SnapshotSessionIdentityChanged',1;
ALTER DATABASE " + MssqlExportMetadataReader.Quote(fresh.Identity.DatabaseName) +
                " SET ALLOW_SNAPSHOT_ISOLATION " + (enabled ? "ON" : "OFF"), cancellation))
            {
                command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
                command.Parameters.Add("@server", SqlDbType.NVarChar, 128).Value = lockedIdentity.ServerName;
                command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = lockedIdentity.DatabaseName;
                command.Parameters.Add("@id", SqlDbType.Int).Value = lockedIdentity.DatabaseId;
                command.Parameters.Add("@created", SqlDbType.DateTime).Value = lockedIdentity.DatabaseCreateDate;
                try { command.ExecuteNonQueryAsync(cancellation).GetAwaiter().GetResult(); }
                catch (SqlException ex) when (ex.Number == 50000 && ex.Message == "SnapshotSessionIdentityChanged")
                { throw new MssqlSnapshotSettingNotAttemptedException(); }
            }
        }
        public void ReleaseLock(CancellationToken cancellation)
        {
            if (!lockHeld) return;
            if (!IsUsable) throw new InvalidOperationException("SnapshotSessionUnavailable");
            using (var command = Command(@"DECLARE @result int;
EXEC @result=sys.sp_releaseapplock @Resource=@resource,@LockOwner='Session',@DbPrincipal='public'; SELECT @result;", cancellation))
            {
                command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
                if (Convert.ToInt32(command.ExecuteScalarAsync(cancellation).GetAwaiter().GetResult()) < 0) throw new InvalidOperationException("SnapshotLockReleaseFailed");
                lockHeld = false;
            }
        }
        private SqlCommand Command(string sql, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!IsUsable) throw new InvalidOperationException("SnapshotSessionUnavailable");
            int timeout = deadline == null ? 30 : deadline.RemainingSeconds;
            return new SqlCommand(sql, connection, transaction) { CommandTimeout = timeout };
        }
    }
}
