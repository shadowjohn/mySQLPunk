using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Providers;

public static class DatabaseProviderFactory
{
    public static IDatabaseSession Create(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        return profile.Provider switch
        {
            DatabaseProviderKind.MySql => new MySqlDatabaseSession(profile.Clone()),
            DatabaseProviderKind.PostgreSql => new PostgreSqlDatabaseSession(profile.Clone()),
            DatabaseProviderKind.Sqlite => new SqliteDatabaseSession(profile.Clone()),
            DatabaseProviderKind.SqlServer => new SqlServerDatabaseSession(profile.Clone()),
            _ => throw new NotSupportedException($"尚未支援資料庫類型：{profile.Provider}")
        };
    }
}
