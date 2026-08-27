using System.Text.Json.Serialization;

namespace MySqlPunk.Core.Models;

public enum DatabaseProviderKind
{
    MySql,
    PostgreSql,
    Sqlite
}

public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "新增連線";

    public DatabaseProviderKind Provider { get; set; } = DatabaseProviderKind.MySql;

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 3306;

    public string Username { get; set; } = string.Empty;

    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public bool UseSsl { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    [JsonIgnore]
    public string ProviderDisplayName => Provider switch
    {
        DatabaseProviderKind.MySql => "MySQL / MariaDB",
        DatabaseProviderKind.PostgreSql => "PostgreSQL",
        DatabaseProviderKind.Sqlite => "SQLite",
        _ => Provider.ToString()
    };

    public ConnectionProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Provider = Provider,
        Host = Host,
        Port = Port,
        Username = Username,
        Password = Password,
        Database = Database,
        UseSsl = UseSsl,
        TimeoutSeconds = TimeoutSeconds
    };

    public void ApplyProviderDefaults(bool resetPort = false)
    {
        if (Provider == DatabaseProviderKind.Sqlite)
        {
            Host = string.Empty;
            Port = 0;
            Username = string.Empty;
            Password = string.Empty;
            UseSsl = false;
            return;
        }

        Host = string.IsNullOrWhiteSpace(Host) ? "localhost" : Host.Trim();
        if (resetPort || Port <= 0)
        {
            Port = Provider == DatabaseProviderKind.PostgreSql ? 5432 : 3306;
        }
    }

    public void Validate()
    {
        Name = Name.Trim();
        Database = Database.Trim();
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 300);

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("連線名稱不可空白。");
        }

        ApplyProviderDefaults();

        if (Provider == DatabaseProviderKind.Sqlite)
        {
            if (string.IsNullOrWhiteSpace(Database))
            {
                throw new InvalidOperationException("SQLite 必須指定資料庫檔案。");
            }

            return;
        }

        Username = Username.Trim();
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException("主機不可空白。");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("連接埠必須介於 1 到 65535。");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            throw new InvalidOperationException("使用者名稱不可空白。");
        }
    }
}
