using System;
using System.Data.SqlClient;
using System.Threading;

namespace mySQLPunk.lib.MssqlExport
{
    // The provider owner also owns any SSH lease; never dispose its connection separately.
    internal sealed class MssqlOwnedSession : IDisposable
    {
        private IDatabase owner;
        internal SqlConnection Connection { get; private set; }

        internal MssqlOwnedSession(IDatabase owner, SqlConnection connection)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            this.owner = owner;
            Connection = connection;
        }

        public void Dispose()
        {
            IDatabase owned = Interlocked.Exchange(ref owner, null);
            if (owned != null) owned.Dispose();
        }
    }
}
