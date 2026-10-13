using System;
using System.Threading;
namespace mySQLPunk.lib.MssqlExport
{
    internal sealed class MssqlSnapshotIdentity
    {
        public string ServerName { get; }
        public string DatabaseName { get; }
        public int DatabaseId { get; }
        public DateTime DatabaseCreateDate { get; }
        public bool IsComplete => !string.IsNullOrWhiteSpace(ServerName) && !string.IsNullOrWhiteSpace(DatabaseName) && DatabaseId > 0 && DatabaseCreateDate != default(DateTime);
        internal MssqlSnapshotIdentity(string serverName, string databaseName, int databaseId, DateTime databaseCreateDate)
        { ServerName = serverName; DatabaseName = databaseName; DatabaseId = databaseId; DatabaseCreateDate = databaseCreateDate; }
    }
    internal enum MssqlSnapshotUnavailableReason { None, IdentityUnknown, StateUnavailable, AlterUnknown, AlterDenied, UpdateabilityUnknown, ReadOnly }
    internal sealed class MssqlSnapshotCapability
    {
        public MssqlSnapshotIdentity Identity { get; }
        public int State { get; }
        public bool? CanAlter { get; }
        public bool? IsWritable { get; }
        public bool CanManage => UnavailableReason == MssqlSnapshotUnavailableReason.None;
        public MssqlSnapshotUnavailableReason UnavailableReason => Identity == null || !Identity.IsComplete ? MssqlSnapshotUnavailableReason.IdentityUnknown :
            State != 0 && State != 1 ? MssqlSnapshotUnavailableReason.StateUnavailable :
            !CanAlter.HasValue ? MssqlSnapshotUnavailableReason.AlterUnknown : !CanAlter.Value ? MssqlSnapshotUnavailableReason.AlterDenied :
            !IsWritable.HasValue ? MssqlSnapshotUnavailableReason.UpdateabilityUnknown : !IsWritable.Value ? MssqlSnapshotUnavailableReason.ReadOnly : MssqlSnapshotUnavailableReason.None;
        internal MssqlSnapshotCapability(MssqlSnapshotIdentity identity, int state, bool? canAlter, bool? isWritable)
        { Identity = identity; State = state; CanAlter = canAlter; IsWritable = isWritable; }
    }
    internal enum MssqlSnapshotCleanupState { NotRequired, PreservedOn, RestoredOff, Unconfirmed }
    internal sealed class MssqlSnapshotCleanupResult
    {
        public MssqlSnapshotCleanupState State { get; }
        public int? ObservedState { get; }
        public string RecoveryRecordPath { get; }
        public string WarningCode { get; }
        internal MssqlSnapshotCleanupResult(MssqlSnapshotCleanupState state, int? observedState, string recoveryRecordPath, string warningCode)
        { State = state; ObservedState = observedState; RecoveryRecordPath = recoveryRecordPath; WarningCode = warningCode; }
    }
    internal interface IMssqlSnapshotBackend
    {
        MssqlSnapshotCapability ReadCapability(CancellationToken cancellation);
        bool TryAcquireLock(CancellationToken cancellation);
        void SetEnabled(bool enabled, CancellationToken cancellation);
        void ReleaseLock(CancellationToken cancellation);
        bool IsUsable { get; }
    }
}
