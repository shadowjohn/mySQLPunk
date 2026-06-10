using System;
using System.Collections.Generic;
using System.IO;
using MySqlConnector;

namespace mySQLPunk.lib
{
    public sealed class ConnectionOpenResult
    {
        public IDatabase Database { get; private set; }
        public List<string> Databases { get; private set; }

        public ConnectionOpenResult(IDatabase database, List<string> databases)
        {
            Database = database;
            Databases = databases ?? new List<string>();
        }
    }

    public static class ConnectionOpenService
    {
        public static ConnectionOpenResult Open(Func<IDatabase> databaseFactory, string connectionString)
        {
            if (databaseFactory == null) throw new ArgumentNullException(nameof(databaseFactory));

            IDatabase db = databaseFactory();
            if (db == null) throw new InvalidOperationException(Localization.T("Connection.DatabaseFactoryReturnedNull"));

            try
            {
                db.SetConn(connectionString);
                db.Open();
                return new ConnectionOpenResult(db, db.GetDatabases());
            }
            catch
            {
                try { db.Dispose(); } catch { }
                throw;
            }
        }

        public static bool ShouldOfferRetry(Exception ex)
        {
            MySqlException mySqlEx = ex as MySqlException;
            if (mySqlEx != null)
            {
                if (mySqlEx.Number == 1045) return false;
                return mySqlEx.IsTransient;
            }

            string message = ex == null ? string.Empty : ex.Message ?? string.Empty;
            if (message.IndexOf("28P01", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (message.IndexOf("password authentication failed", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (message.IndexOf("login failed for user", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (message.IndexOf("ORA-01017", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (message.IndexOf("ORA-28000", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            if (ex is TimeoutException) return true;
            if (ex is IOException) return true;
            if (ex is System.Net.Sockets.SocketException) return true;
            if (ex != null && ex.InnerException != null) return ShouldOfferRetry(ex.InnerException);
            return false;
        }
    }
}
