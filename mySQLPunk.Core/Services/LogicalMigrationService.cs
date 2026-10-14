using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;

namespace MySqlPunk.Core.Services;

public static class LogicalMigrationService
{
    public const int MaximumRows = 100_000;
    private const long MaximumBytes = 64 * 1024 * 1024;
    private static readonly Regex Identifier = new("\\A[A-Za-z_][A-Za-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);
    private sealed record Snapshot(string Version, string Identity, List<LogicalMigrationTable> Tables,
        Dictionary<string, List<object?[]>> Data, List<string> Blockers);

    public static Task<LogicalMigrationPreview> PreviewAsync(ConnectionProfile source, ConnectionProfile target,
        int selectedTargetMajor, CancellationToken cancellationToken = default) =>
        Task.Run(() => PreviewCoreAsync(source.Clone(), target.Clone(), selectedTargetMajor, cancellationToken), cancellationToken);

    public static Task<LogicalMigrationResult> RunAsync(ConnectionProfile source, ConnectionProfile target,
        int selectedTargetMajor, LogicalMigrationPreview approved, string confirmation,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RunCoreAsync(source.Clone(), target.Clone(), selectedTargetMajor, approved, confirmation, cancellationToken, null), cancellationToken);

    internal static Task<LogicalMigrationResult> RunWithObserverAsync(ConnectionProfile source, ConnectionProfile target,
        int selectedTargetMajor, LogicalMigrationPreview approved, string confirmation,
        CancellationToken token, Action<string, DbConnection, DbTransaction> observer) =>
        Task.Run(() => RunCoreAsync(source.Clone(), target.Clone(), selectedTargetMajor, approved, confirmation, token, observer), token);

    private static bool Supported(ConnectionProfile profile) =>
        profile.Provider is DatabaseProviderKind.Sqlite or DatabaseProviderKind.PostgreSql;

    private static LogicalMigrationPreview Refused(ConnectionProfile target, string reason) =>
        new("未識別", "未識別", target.Database, Array.Empty<LogicalMigrationTable>(), Array.Empty<string>(), new[] { reason }, "");

    private static async Task<LogicalMigrationPreview> PreviewCoreAsync(ConnectionProfile source, ConnectionProfile target,
        int selectedTargetMajor, CancellationToken token)
    {
        if (!Supported(source) || !Supported(target))
            return Refused(target, "此階段只實作 SQLite STRICT 與 PostgreSQL 的整數／二進位邏輯搬移。其他引擎請使用專用搬移器；不可當成原生 restore。");
        using var sourceOwner = (AdoDatabaseSession)DatabaseProviderFactory.Create(source);
        using var targetOwner = (AdoDatabaseSession)DatabaseProviderFactory.Create(target);
        await using var origin = await sourceOwner.OpenMigrationConnectionAsync(source.Database, token, readOnly: true).ConfigureAwait(false);
        await using var destination = await targetOwner.OpenMigrationConnectionAsync(target.Database, token).ConfigureAwait(false);
        using var sourceProgress = new SqliteProgress(origin, token);
        using var targetProgress = new SqliteProgress(destination, token);
        await using var sourceTransaction = await origin.BeginTransactionAsync(source.Provider == DatabaseProviderKind.Sqlite ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
        try
        {
            var sourceSnapshot = await ReadSnapshotAsync(origin, sourceTransaction, source.Provider, true, token).ConfigureAwait(false);
            var targetSnapshot = await ReadSnapshotAsync(destination, null, target.Provider, false, token).ConfigureAwait(false);
            return BuildPreview(source, target, selectedTargetMajor, sourceSnapshot, targetSnapshot);
        }
        finally { sourceProgress.Stop(); targetProgress.Stop(); }
    }

    private static LogicalMigrationPreview BuildPreview(ConnectionProfile source, ConnectionProfile target,
        int selectedTargetMajor, Snapshot origin, Snapshot destination)
    {
        var blockers = origin.Blockers.Select(reason => "來源：" + reason)
            .Concat(destination.Blockers.Select(reason => "目標：" + reason)).ToList();
        if (destination.Tables.Count != 0) blockers.Add("目標必須是另建的空資料庫，不能覆蓋或續寫既有表。");
        if (origin.Tables.Count == 0) blockers.Add("來源沒有可搬移資料表。");
        if (source.Provider == target.Provider && origin.Identity == destination.Identity) blockers.Add("實際引擎識別為同一資料庫，不允許搬移至自身。");
        if (target.Provider == DatabaseProviderKind.Sqlite && (origin.Tables.GroupBy(table => table.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
            origin.Tables.Any(table => table.Columns.GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))))
            blockers.Add("來源名稱只差大小寫；SQLite 無法忠實保存這些不同的表／欄位名稱。");
        if (Major(destination.Version) != selectedTargetMajor) blockers.Add("明確選取的目標版本與實際引擎不符。");
        if (source.Provider == target.Provider && source.Database == target.Database &&
            (source.Provider == DatabaseProviderKind.Sqlite || source.Host == target.Host && source.Port == target.Port))
            blockers.Add("來源與目標不得為同一資料庫。");
        var statements = origin.Tables.Select(table => CreateTableSql(target.Provider, table)).ToArray();
        var fingerprint = Hash(JsonSerializer.Serialize(new
        {
            Source = new { source.Provider, source.Host, source.Port, source.Database },
            Target = new { target.Provider, target.Host, target.Port, target.Database },
            origin.Version, origin.Identity, TargetIdentity = destination.Identity, TargetVersion = destination.Version, origin.Tables, Statements = statements, Blockers = blockers
        }));
        return new(origin.Version, destination.Version, target.Database, origin.Tables.AsReadOnly(), statements,
            blockers.AsReadOnly(), fingerprint);
    }

    private static async Task<LogicalMigrationResult> RunCoreAsync(ConnectionProfile source, ConnectionProfile target,
        int selectedTargetMajor, LogicalMigrationPreview approved, string confirmation, CancellationToken token,
        Action<string, DbConnection, DbTransaction>? observer)
    {
        if (!approved.CanStart || confirmation != target.Database || !Supported(source) || !Supported(target))
            return new(LogicalMigrationOutcome.Refused, 0, "未確認完整目標名稱或預檢未通過；沒有寫入。");
        using var sourceOwner = (AdoDatabaseSession)DatabaseProviderFactory.Create(source);
        using var targetOwner = (AdoDatabaseSession)DatabaseProviderFactory.Create(target);
        await using var origin = await sourceOwner.OpenMigrationConnectionAsync(source.Database, token, readOnly: true).ConfigureAwait(false);
        await using var destination = await targetOwner.OpenMigrationConnectionAsync(target.Database, token).ConfigureAwait(false);
        using var sourceProgress = new SqliteProgress(origin, token);
        using var targetProgress = new SqliteProgress(destination, token);
        await using var sourceTransaction = await origin.BeginTransactionAsync(source.Provider == DatabaseProviderKind.Sqlite ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
        await using var targetTransaction = destination is SqliteConnection sqlite
            ? sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: false)
            : await destination.BeginTransactionAsync(IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
        bool commitAttempted = false;
        long rows = 0;
        string stage = "交易內重新預檢";
        try
        {
            if (target.Provider == DatabaseProviderKind.PostgreSql)
                await ExecuteAsync(destination, targetTransaction, "SELECT pg_advisory_xact_lock(1836281931);", token).ConfigureAwait(false);
            var sourceSnapshot = await ReadSnapshotAsync(origin, sourceTransaction, source.Provider, true, token).ConfigureAwait(false);
            var targetSnapshot = await ReadSnapshotAsync(destination, targetTransaction, target.Provider, false, token).ConfigureAwait(false);
            var current = BuildPreview(source, target, selectedTargetMajor, sourceSnapshot, targetSnapshot);
            if (!current.CanStart || current.Fingerprint != approved.Fingerprint)
                throw new InvalidOperationException("來源內容／結構／版本或目標在確認後改變，請重新預檢。");
            for (var tableIndex = 0; tableIndex < sourceSnapshot.Tables.Count; tableIndex++)
            {
                var table = sourceSnapshot.Tables[tableIndex];
                stage = "建立與傳輸 " + table.Name;
                await ExecuteAsync(destination, targetTransaction, current.Statements[tableIndex], token).ConfigureAwait(false);
                var data = sourceSnapshot.Data[table.Name];
                var batchSize = Math.Max(1, Math.Min(100, 200 / table.Columns.Count));
                foreach (var batch in data.Chunk(batchSize))
                {
                    token.ThrowIfCancellationRequested();
                    await using var command = destination.CreateCommand();
                    command.Transaction = targetTransaction;
                    var values = new List<string>();
                    foreach (var row in batch)
                    {
                        var parameters = new List<string>();
                        for (var columnIndex = 0; columnIndex < row.Length; columnIndex++)
                        {
                            var name = "@value" + command.Parameters.Count;
                            var parameter = command.CreateParameter();
                            parameter.ParameterName = name;
                            parameter.DbType = table.Columns[columnIndex].Kind == "integer" ? DbType.Int64 : DbType.Binary;
                            parameter.Value = row[columnIndex] ?? DBNull.Value;
                            command.Parameters.Add(parameter);
                            parameters.Add(name);
                        }
                        values.Add("(" + string.Join(",", parameters) + ")");
                    }
                    command.CommandText = "INSERT INTO " + Qualified(target.Provider, table.Name) + " (" +
                        string.Join(",", table.Columns.Select(column => Quote(column.Name))) + ") VALUES " + string.Join(",", values);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    rows += batch.Length;
                    observer?.Invoke("batch", destination, targetTransaction);
                }
            }
            stage = "逐欄型別、NULL、內容與結構驗證";
            observer?.Invoke("verify", destination, targetTransaction);
            var written = await ReadSnapshotAsync(destination, targetTransaction, target.Provider, true, token).ConfigureAwait(false);
            if (written.Blockers.Count != 0 || JsonSerializer.Serialize(written.Tables) != JsonSerializer.Serialize(sourceSnapshot.Tables))
                throw new InvalidOperationException("目標結構或內容 SHA-256 多重集合驗證不符。");
            var sourceFinal = await ReadSnapshotAsync(origin, sourceTransaction, source.Provider, true, token).ConfigureAwait(false);
            if (sourceFinal.Blockers.Count != 0 || JsonSerializer.Serialize(sourceFinal.Tables) != JsonSerializer.Serialize(sourceSnapshot.Tables))
                throw new InvalidOperationException("來源結構／內容在搬移期間改變。");
            token.ThrowIfCancellationRequested();
            sourceProgress.Stop();
            await sourceTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            stage = "提交目標";
            targetProgress.Stop();
            token.ThrowIfCancellationRequested();
            commitAttempted = true;
            await targetTransaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            observer?.Invoke("committed", destination, targetTransaction);
            return new(LogicalMigrationOutcome.Verified, rows, "單一交易已提交；結構、列數與逐欄型別／NULL／內容 SHA-256 多重集合全部相符。不自動再次寫入。");
        }
        catch (Exception exception)
        {
            sourceProgress.Stop(); targetProgress.Stop();
            bool rollbackConfirmed = !commitAttempted;
            try { await targetTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { rollbackConfirmed = false; }
            return new(rollbackConfirmed ? LogicalMigrationOutcome.RolledBack : LogicalMigrationOutcome.Uncertain, rows,
                (token.IsCancellationRequested ? "已取消。" : "搬移失敗。") + "階段：" + stage + "；" + exception.GetType().Name +
                (rollbackConfirmed ? "；目標交易已回滾。重新預檢後才能重試，沒有逐表 checkpoint 續寫。" : "；提交／清理結果不確定，禁止盲目重試，請先由 DBA 核對目標。"));
        }
    }

    private static async Task<Snapshot> ReadSnapshotAsync(DbConnection connection, DbTransaction? transaction,
        DatabaseProviderKind provider, bool readData, CancellationToken token)
    {
        var blockers = new List<string>();
        var version = Convert.ToString(await ScalarAsync(connection, transaction,
            provider == DatabaseProviderKind.Sqlite ? "SELECT sqlite_version();" : "SHOW server_version;", token), CultureInfo.InvariantCulture) ?? "";
        var identity = connection is SqliteConnection sqlite ? Path.GetFullPath(sqlite.DataSource) :
            Convert.ToString(await ScalarAsync(connection, transaction,
                "SELECT current_database() || ':' || (SELECT oid::text FROM pg_database WHERE datname=current_database()) || ':' || coalesce(inet_server_addr()::text,'local') || ':' || coalesce(inet_server_port()::text,'local');", token)) ?? "";
        if (identity.Length == 0) blockers.Add("無法確認實際資料庫識別。");
        if (provider == DatabaseProviderKind.Sqlite)
        {
            if (!Version.TryParse(version.Split(' ')[0], out var parsed) || parsed.Major != 3 || parsed.Minor < 37 || parsed.Minor > 53)
                blockers.Add("只允許 SQLite 3.37–3.53；其他版本尚未建立能力矩陣。");
            var encoding = await ScalarAsync(connection, transaction, "PRAGMA encoding;", token);
            if (!Equals(encoding, "UTF-8")) blockers.Add("只允許 UTF-8 SQLite 資料庫。");
        }
        else
        {
            if (Major(version) is < 14 or > 17) blockers.Add("只允許 PostgreSQL 14–17；其他版本尚未建立能力矩陣。");
            if (!Equals(await ScalarAsync(connection, transaction, "SHOW server_encoding;", token), "UTF8")) blockers.Add("只允許 UTF8 PostgreSQL 資料庫。");
            if (Convert.ToInt64(await ScalarAsync(connection, transaction, PostgreSqlUnsupportedCatalogSql, token)) != 0)
                blockers.Add("發現不支援的 schema／extension／view／sequence／index／constraint／default／identity／trigger／procedure／type／partition／RLS／inheritance 或表屬性；不能靜默略過。");
        }
        var definitions = new List<(string Name, string Definition)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = provider == DatabaseProviderKind.Sqlite
                ? "SELECT name, type, coalesce(sql,'') FROM sqlite_schema ORDER BY name;"
                : "SELECT c.relname, 'table', '' FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind='r' ORDER BY c.relname COLLATE \"C\";";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                if (reader.GetString(1) != "table" || name.StartsWith("sqlite_", StringComparison.Ordinal))
                    blockers.Add("不支援物件：" + name + "（" + reader.GetString(1) + "）");
                else definitions.Add((name, reader.GetString(2)));
            }
        }
        var tables = new List<LogicalMigrationTable>();
        var data = new Dictionary<string, List<object?[]>>();
        long totalRows = 0, totalBytes = 0;
        if (readData && provider == DatabaseProviderKind.PostgreSql && blockers.Count == 0)
            foreach (var definition in definitions)
                await ExecuteAsync(connection, transaction, "LOCK TABLE " + Qualified(provider, definition.Name) + " IN SHARE MODE;", token).ConfigureAwait(false);
        foreach (var definition in definitions)
        {
            token.ThrowIfCancellationRequested();
            if (!Identifier.IsMatch(definition.Name)) { blockers.Add("表名不在 ASCII 63 字元安全子集：" + definition.Name); continue; }
            var columns = await ReadColumnsAsync(connection, transaction, provider, definition.Name, blockers, token).ConfigureAwait(false);
            if (columns.Count == 0 || columns.Count > 100) { blockers.Add("欄位數必須為 1–100：" + definition.Name); continue; }
            if (provider == DatabaseProviderKind.Sqlite && !SafeSqliteDefinition(definition.Name, columns, definition.Definition))
                blockers.Add("僅接受無鍵／索引／default／collation／生成欄位的 INTEGER、BLOB STRICT 表：" + definition.Name);
            var rows = new List<object?[]>();
            if (readData && blockers.Count == 0)
            {
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT " + string.Join(",", columns.Select(column => Quote(column.Name))) + " FROM " + Qualified(provider, definition.Name);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (++totalRows > MaximumRows) throw new InvalidOperationException("來源超過 100,000 列，請使用專用搬移器。");
                    var row = new object?[columns.Count];
                    for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                    {
                        var value = reader.IsDBNull(columnIndex) ? null : reader.GetValue(columnIndex);
                        if (value != null && (columns[columnIndex].Kind == "integer" ? value is not long : value is not byte[]))
                            throw new InvalidOperationException("實際儲存型別與宣告不符。");
                        var bytes = value is byte[] binary ? binary.Length : 8;
                        totalBytes += bytes;
                        if (bytes > 1024 * 1024 || totalBytes > MaximumBytes) throw new InvalidOperationException("超過單欄 1 MiB／總量 64 MiB 預覽上限。");
                        row[columnIndex] = value;
                    }
                    rows.Add(row);
                }
            }
            tables.Add(new(definition.Name, columns.AsReadOnly(), rows.Count, ContentHash(rows)));
            data.Add(definition.Name, rows);
        }
        return new(version, identity, tables, data, blockers);
    }

    internal const string PostgreSqlUnsupportedCatalogSql = """
        SELECT
        (SELECT count(*) FROM pg_namespace WHERE nspname NOT IN ('public','information_schema','pg_catalog') AND nspname NOT LIKE 'pg_toast%' AND nspname NOT LIKE 'pg_temp_%') +
        (SELECT count(*) FROM pg_extension WHERE extname <> 'plpgsql') +
        (SELECT count(*) FROM pg_event_trigger) +
        (SELECT count(*) FROM pg_largeobject_metadata) +
        (SELECT count(*) FROM pg_publication) +
        (SELECT count(*) FROM pg_subscription WHERE subdbid=(SELECT oid FROM pg_database WHERE datname=current_database())) +
        (SELECT count(*) FROM pg_collation c JOIN pg_namespace n ON n.oid=c.collnamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_conversion c JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_operator o JOIN pg_namespace n ON n.oid=o.oprnamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_opclass o JOIN pg_namespace n ON n.oid=o.opcnamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_opfamily o JOIN pg_namespace n ON n.oid=o.opfnamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_ts_config c JOIN pg_namespace n ON n.oid=c.cfgnamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_ts_dict d JOIN pg_namespace n ON n.oid=d.dictnamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_rewrite r JOIN pg_class c ON c.oid=r.ev_class JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_policy p JOIN pg_class c ON c.oid=p.polrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace WHERE n.nspname='public' AND t.typtype NOT IN ('c') AND NOT (t.typcategory='A' AND EXISTS (SELECT 1 FROM pg_type base JOIN pg_class c ON c.oid=base.typrelid WHERE base.oid=t.typelem AND c.relkind='r'))) +
        (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND (c.relkind<>'r' OR c.relpersistence<>'p' OR c.relispartition OR c.relrowsecurity OR c.relforcerowsecurity OR c.reloptions IS NOT NULL OR c.reltablespace<>0)) +
        (SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_attrdef a JOIN pg_class c ON c.oid=a.adrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_inherits i JOIN pg_class c ON c.oid=i.inhrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public') +
        (SELECT count(*) FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND a.attnum>0 AND (a.attisdropped OR a.attidentity<>'' OR a.attgenerated<>'' OR a.attcollation<>0 OR a.atttypid NOT IN (20,17)));
        """;

    private static async Task<List<LogicalMigrationColumn>> ReadColumnsAsync(DbConnection connection, DbTransaction? transaction,
        DatabaseProviderKind provider, string table, List<string> blockers, CancellationToken token)
    {
        var columns = new List<LogicalMigrationColumn>();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = provider == DatabaseProviderKind.Sqlite ? "PRAGMA table_xinfo(" + Quote(table) + ");"
            : "SELECT a.attname, format_type(a.atttypid,a.atttypmod), a.attnotnull FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relname=@table AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum;";
        if (provider != DatabaseProviderKind.Sqlite) { var parameter = command.CreateParameter(); parameter.ParameterName = "@table"; parameter.Value = table; command.Parameters.Add(parameter); }
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var name = reader.GetString(provider == DatabaseProviderKind.Sqlite ? 1 : 0);
            var type = reader.GetString(provider == DatabaseProviderKind.Sqlite ? 2 : 1).ToLowerInvariant();
            var kind = type is "integer" or "bigint" ? "integer" : type is "blob" or "bytea" ? "binary" : "unsupported";
            if (!Identifier.IsMatch(name) || kind == "unsupported") blockers.Add("不支援欄位名稱／型別：" + table + "." + name + " " + type);
            var notNull = provider == DatabaseProviderKind.Sqlite ? reader.GetInt64(3) != 0 : reader.GetBoolean(2);
            columns.Add(new(name, kind, !notNull));
        }
        return columns;
    }

    private static bool SafeSqliteDefinition(string name, List<LogicalMigrationColumn> columns, string sql)
    {
        static string NamePattern(string identifier) => "(?:" + Regex.Escape(identifier) + "|\"" + Regex.Escape(identifier) + "\")";
        var definitions = columns.Select(column => NamePattern(column.Name) + "\\s+" +
            (column.Kind == "integer" ? "INTEGER" : "BLOB") + (column.Nullable ? "" : "\\s+NOT\\s+NULL"));
        return Regex.IsMatch(sql, "\\ACREATE\\s+TABLE\\s+" + NamePattern(name) + "\\s*\\(\\s*" +
            string.Join("\\s*,\\s*", definitions) + "\\s*\\)\\s+STRICT\\s*;?\\s*\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string CreateTableSql(DatabaseProviderKind provider, LogicalMigrationTable table) =>
        "CREATE TABLE " + Qualified(provider, table.Name) + " (" + string.Join(",", table.Columns.Select(column =>
            Quote(column.Name) + " " + (column.Kind == "integer" ? provider == DatabaseProviderKind.Sqlite ? "INTEGER" : "bigint"
                : provider == DatabaseProviderKind.Sqlite ? "BLOB" : "bytea") + (column.Nullable ? "" : " NOT NULL"))) +
        ")" + (provider == DatabaseProviderKind.Sqlite ? " STRICT;" : ";");
    private static int Major(string version) => int.TryParse(version.Split('.')[0], out var major) ? major : -1;
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string Qualified(DatabaseProviderKind provider, string table) => (provider == DatabaseProviderKind.PostgreSql ? "\"public\"." : "") + Quote(table);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string ContentHash(List<object?[]> rows) => Hash(string.Join("\n", rows.Select(row =>
        Hash(string.Join("|", row.Select(value => value == null ? "null" : value is byte[] binary ? "binary:" + Convert.ToBase64String(binary)
            : "integer:" + ((long)value).ToString(CultureInfo.InvariantCulture))))).OrderBy(value => value, StringComparer.Ordinal)));
    private static async Task<object?> ScalarAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }
    private static async Task ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private sealed class SqliteProgress : IDisposable
    {
        private readonly SqliteConnection? _connection;
        public SqliteProgress(DbConnection connection, CancellationToken token)
        {
            _connection = connection as SqliteConnection;
            if (_connection != null) SQLitePCL.raw.sqlite3_progress_handler(_connection.Handle!, 1000, _ => token.IsCancellationRequested ? 1 : 0, null);
        }
        public void Stop()
        {
            if (_connection != null) SQLitePCL.raw.sqlite3_progress_handler(_connection.Handle!, 0, null, null);
        }
        public void Dispose() => Stop();
    }
}
