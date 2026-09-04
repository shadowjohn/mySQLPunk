using Microsoft.Data.SqlClient;
using MySqlConnector;
using MySqlPunk.Core.Models;
using Npgsql;

namespace MySqlPunk.Core.Services;

public static class ConnectionTlsModeMapper
{
    public static MySqlSslMode ToMySql(ConnectionTlsMode mode) => mode switch
    {
        ConnectionTlsMode.Default or ConnectionTlsMode.Preferred => MySqlSslMode.Preferred,
        ConnectionTlsMode.Disabled => MySqlSslMode.None,
        ConnectionTlsMode.Required => MySqlSslMode.Required,
        ConnectionTlsMode.VerifyCertificateAuthority => MySqlSslMode.VerifyCA,
        ConnectionTlsMode.VerifyFull => MySqlSslMode.VerifyFull,
        _ => throw Unsupported(DatabaseProviderKind.MySql, mode)
    };

    public static SslMode ToPostgreSql(ConnectionTlsMode mode) => mode switch
    {
        ConnectionTlsMode.Default or ConnectionTlsMode.Preferred => SslMode.Prefer,
        ConnectionTlsMode.Disabled => SslMode.Disable,
        ConnectionTlsMode.Allow => SslMode.Allow,
        ConnectionTlsMode.Required => SslMode.Require,
        ConnectionTlsMode.VerifyCertificateAuthority => SslMode.VerifyCA,
        ConnectionTlsMode.VerifyFull => SslMode.VerifyFull,
        _ => throw Unsupported(DatabaseProviderKind.PostgreSql, mode)
    };

    public static SqlConnectionEncryptOption ToSqlServer(ConnectionTlsMode mode) => mode switch
    {
        ConnectionTlsMode.Default or ConnectionTlsMode.Mandatory => SqlConnectionEncryptOption.Mandatory,
        ConnectionTlsMode.Optional => SqlConnectionEncryptOption.Optional,
        ConnectionTlsMode.Strict => SqlConnectionEncryptOption.Strict,
        _ => throw Unsupported(DatabaseProviderKind.SqlServer, mode)
    };

    private static InvalidOperationException Unsupported(
        DatabaseProviderKind provider,
        ConnectionTlsMode mode) => new($"{provider} 不支援 TLS 模式 {mode}。");
}
