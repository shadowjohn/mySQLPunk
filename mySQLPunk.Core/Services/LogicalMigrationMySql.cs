using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;

namespace MySqlPunk.Core.Services;

public static partial class LogicalMigrationService
{
    internal static Task<LogicalMigrationResult> RunWithMySqlObserverAsync(ConnectionProfile source, ConnectionProfile target,
        int selectedTargetMajor, LogicalMigrationPreview approved, CancellationToken token, Action<string, DbConnection, DbTransaction?> observer) =>
        Task.Run(() => RunCoreAsync(source.Clone(), target.Clone(), selectedTargetMajor, approved, target.Database, token, null, observer), token);

    private static IsolationLevel SourceIsolation(DatabaseProviderKind provider) => provider switch
    {
        DatabaseProviderKind.Sqlite => IsolationLevel.Serializable,
        DatabaseProviderKind.MySql => IsolationLevel.RepeatableRead,
        _ => IsolationLevel.ReadCommitted
    };

    private static string MySqlQuote(string name) => "`" + name.Replace("`", "``") + "`";
    private static bool IsMariaDb(string version) => version.Contains("MariaDB", StringComparison.Ordinal);
    private static string MySqlCollation(string version) => IsMariaDb(version) ? "utf8mb4_nopad_bin" : "utf8mb4_0900_bin";

    private static void ValidateMySqlDirection(ConnectionProfile source, ConnectionProfile target, Snapshot origin, Snapshot destination, List<string> blockers)
    {
        if (source.Provider != DatabaseProviderKind.PostgreSql && target.Provider != DatabaseProviderKind.PostgreSql)
            blockers.Add("MySQL／MariaDB 本階段只允許與 PostgreSQL 雙向邏輯搬移，不開放其他配對。");
        if (origin.Tables.Count > 100) blockers.Add("MySQL 配對最多 100 表，限制單次 staging 與 RENAME 語句規模。");
        if (target.Provider == DatabaseProviderKind.MySql &&
            (destination.PacketBytes < 65536 || origin.Data.Values.SelectMany(rows => rows).Any(row => MySqlRowBytes(row) + 16384 > MySqlBatchBytes(destination.PacketBytes))))
            blockers.Add("資料列超出目標 max_allowed_packet 的保守封包預算（含 UTF-8／binary escaping），不自動修改伺服器設定。");
        foreach (var table in origin.Tables)
        {
            if (!Regex.IsMatch(table.Name, "\\A[a-z_][a-z0-9_]{0,62}\\z") || table.Columns.Any(column => !Regex.IsMatch(column.Name, "\\A[a-z_][a-z0-9_]{0,62}\\z")) ||
                table.Indexes.Any(index => !Regex.IsMatch(index.Name, "\\A[a-z_][a-z0-9_]{0,62}\\z")))
                blockers.Add(table.Name + "：MySQL 配對只允許小寫 ASCII 名稱，避免大小寫配置差異。");
            var keys = table.Indexes.Select(index => index.Columns).Concat(table.PrimaryKey.Count == 0 ? Array.Empty<IReadOnlyList<string>>() : new[] { table.PrimaryKey });
            if (keys.Any(key => key.Count is < 1 or > 16 || key.Any(name => table.Columns.All(column => column.Name != name || column.Kind != "integer"))))
                blockers.Add(table.Name + "：MySQL 配對只保留最多 16 欄 signed bigint 主鍵／普通索引；TEXT／BLOB 前綴索引不能忠實替代完整鍵。");
            if (target.Provider == DatabaseProviderKind.MySql && table.PrimaryKeyName != null && table.PrimaryKeyName != DefaultPrimaryKeyName(table.Name))
                blockers.Add(table.Name + "：MySQL 的 PRIMARY 無法保留自訂 PostgreSQL constraint 名稱。");
        }
        if (origin.Tables.SelectMany(table => table.Indexes).Select(index => index.Name).GroupBy(name => name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            blockers.Add("MySQL 表內索引名稱在 PostgreSQL 全域名稱空間重複，不能靜默更名。");
    }

    private static string CreateMySqlTableSql(LogicalMigrationTable table, string collation) =>
        "CREATE TABLE " + MySqlQuote(table.Name) + " (" + string.Join(",", table.Columns.Select(column =>
            MySqlQuote(column.Name) + " " + (column.Kind == "integer" ? "bigint" : column.Kind == "binary" ? "longblob" : "longtext CHARACTER SET utf8mb4 COLLATE " + collation) +
            (column.Nullable ? " DEFAULT NULL" : " NOT NULL"))) +
        (table.PrimaryKey.Count == 0 ? "" : ",PRIMARY KEY (" + string.Join(",", table.PrimaryKey.Select(MySqlQuote)) + ")") +
        string.Concat(table.Indexes.Select(index => ",KEY " + MySqlQuote(index.Name) + " (" + string.Join(",", index.Columns.Select(MySqlQuote)) + ")")) +
        ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=" + collation + ";";

    private static async Task<Snapshot> ReadMySqlSnapshotAsync(DbConnection connection, DbTransaction? transaction, bool readData, CancellationToken token)
    {
        var blockers = new List<string>();
        var version = Convert.ToString(await ScalarAsync(connection, transaction, "SELECT VERSION();", token)) ?? "";
        var packetBytes = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT @@max_allowed_packet;", token));
        var validVersion = IsMariaDb(version) ? Regex.IsMatch(version, "\\A11\\.4\\.[0-9]+-MariaDB") : Regex.IsMatch(version, "\\A8\\.4\\.[0-9]+\\z");
        if (!validVersion) blockers.Add("只驗證 MySQL 8.4.x／MariaDB 11.4.x；其他相容伺服器或版本不放行。");
        var collation = MySqlCollation(version);
        if (Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT @@lower_case_table_names;", token)) != 0)
            blockers.Add("要求 lower_case_table_names=0；不更改伺服器設定。");
        var identity = Convert.ToString(await ScalarAsync(connection, transaction, "SELECT CONCAT(DATABASE(),':',@@hostname,':',@@port);", token)) ?? "";
        if (string.IsNullOrEmpty(identity)) blockers.Add("無法確認實際資料庫識別。");
        var encoding = Convert.ToString(await ScalarAsync(connection, transaction,
            "SELECT CONCAT(DEFAULT_CHARACTER_SET_NAME,':',DEFAULT_COLLATION_NAME) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME=DATABASE();", token));
        if (encoding != "utf8mb4:" + collation) blockers.Add("資料庫必須使用 utf8mb4／" + collation + "，不自動修改 encoding／collation。");
        var visiblePrivileges = Convert.ToInt64(await ScalarAsync(connection, transaction, """
            SELECT COUNT(DISTINCT PRIVILEGE_TYPE) FROM (
                SELECT PRIVILEGE_TYPE FROM information_schema.USER_PRIVILEGES
                    WHERE GRANTEE=CONCAT(CHAR(39),SUBSTRING_INDEX(CURRENT_USER(),'@',1),CHAR(39),'@',CHAR(39),SUBSTRING_INDEX(CURRENT_USER(),'@',-1),CHAR(39))
                UNION ALL SELECT PRIVILEGE_TYPE FROM information_schema.SCHEMA_PRIVILEGES
                    WHERE TABLE_SCHEMA=DATABASE() AND GRANTEE=CONCAT(CHAR(39),SUBSTRING_INDEX(CURRENT_USER(),'@',1),CHAR(39),'@',CHAR(39),SUBSTRING_INDEX(CURRENT_USER(),'@',-1),CHAR(39))
            ) privileges WHERE PRIVILEGE_TYPE IN ('SELECT','SHOW VIEW','TRIGGER','EVENT','CREATE ROUTINE','ALTER ROUTINE');
            """, token));
        if (visiblePrivileges != 6) blockers.Add("無法證明目前帳號可完整看見 view／trigger／event／routine catalog（角色／表級授權未支援）；不讀秘密、不增授權。");
        var unsupported = await ScalarAsync(connection, transaction, """
            SELECT
                (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND
                    (TABLE_TYPE<>'BASE TABLE' OR ENGINE<>'InnoDB' OR ROW_FORMAT<>'Dynamic' OR CREATE_OPTIONS<>'' OR TABLE_COMMENT<>'')) +
                (SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=DATABASE()) +
                (SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA=DATABASE()) +
                (SELECT COUNT(*) FROM information_schema.EVENTS WHERE EVENT_SCHEMA=DATABASE()) +
                (SELECT COUNT(*) FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA=DATABASE() AND PARTITION_NAME IS NOT NULL) +
                (SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() AND CONSTRAINT_TYPE<>'PRIMARY KEY');
            """, token);
        if (Convert.ToInt64(unsupported) != 0) blockers.Add("存在非 InnoDB Dynamic 普通表、view、sequence、trigger、routine、event、partition、FK／CHECK／UNIQUE 或自訂表屬性；不能略過。");
        var names = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() ORDER BY BINARY TABLE_NAME;";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) names.Add(reader.GetString(0));
        }
        var tables = new List<LogicalMigrationTable>();
        var data = new Dictionary<string, List<object?[]>>();
        long totalRows = 0, totalBytes = 0;
        foreach (var name in names)
        {
            if (!Identifier.IsMatch(name)) { blockers.Add("表名不在安全子集。"); continue; }
            var columns = new List<LogicalMigrationColumn>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,COLUMN_DEFAULT,EXTRA,COLLATION_NAME,GENERATION_EXPRESSION,COLUMN_COMMENT FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table ORDER BY ORDINAL_POSITION;";
                var parameter = command.CreateParameter(); parameter.ParameterName = "@table"; parameter.Value = name; command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var type = reader.GetString(1);
                    var kind = type is "bigint" or "bigint(20)" ? "integer" : type == "longblob" ? "binary" : type == "longtext" ? "text" : "unsupported";
                    if (!Identifier.IsMatch(reader.GetString(0)) || kind == "unsupported" || (!reader.IsDBNull(3) && !(IsMariaDb(version) && reader.GetString(3) == "NULL")) || reader.GetString(4) != "" ||
                        (!reader.IsDBNull(6) && reader.GetString(6) != "") || reader.GetString(7) != "" ||
                        (kind == "text" ? reader.IsDBNull(5) || reader.GetString(5) != collation : !reader.IsDBNull(5)))
                        blockers.Add(name + "/" + reader.GetString(0) + "：只接受 signed BIGINT／LONGTEXT NO PAD binary／LONGBLOB，無 default／generated／identity／自訂欄位屬性。");
                    columns.Add(new(reader.GetString(0), kind, reader.GetString(2) == "YES"));
                }
            }
            var primary = new List<string>();
            var indexes = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT INDEX_NAME,COLUMN_NAME,NON_UNIQUE,INDEX_TYPE,SUB_PART,COLLATION,INDEX_COMMENT," +
                    (IsMariaDb(version) ? "IGNORED" : "IS_VISIBLE") + " FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table ORDER BY BINARY INDEX_NAME,SEQ_IN_INDEX;";
                var parameter = command.CreateParameter(); parameter.ParameterName = "@table"; parameter.Value = name; command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var indexName = reader.GetString(0);
                    if (reader.IsDBNull(1) || (indexName != "PRIMARY" && !Identifier.IsMatch(indexName)) ||
                        Convert.ToInt32(reader.GetValue(2)) != (indexName == "PRIMARY" ? 0 : 1) || reader.GetString(3) != "BTREE" ||
                        !reader.IsDBNull(4) || reader.IsDBNull(5) || reader.GetString(5) != "A" || reader.GetString(6) != "" ||
                        reader.GetString(7) != (IsMariaDb(version) ? "NO" : "YES"))
                    { blockers.Add(name + "：不支援 unique／prefix／expression／descending／invisible 或非 BTREE 索引。"); continue; }
                    if (indexName == "PRIMARY") primary.Add(reader.GetString(1));
                    else
                    {
                        if (!indexes.TryGetValue(indexName, out var fields)) indexes.Add(indexName, fields = new());
                        fields.Add(reader.GetString(1));
                    }
                }
            }
            if (columns.Count is < 1 or > 100 || primary.Any(field => columns.All(column => column.Name != field || column.Nullable)))
                blockers.Add(name + "：欄位數／主鍵 NOT NULL 不符。");
            var shape = new LogicalMigrationTable(name, columns, 0, "")
            {
                PrimaryKey = primary,
                Indexes = indexes.Select(index => new LogicalMigrationIndex(index.Key, index.Value)).ToArray()
            };
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "SHOW CREATE TABLE " + MySqlQuote(name);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false) || !SafeMySqlDefinition(shape, reader.GetString(1), collation))
                    blockers.Add(name + "：SHOW CREATE TABLE 包含不支援語意或 catalog 與 DDL 不符。");
            }
            var rows = new List<object?[]>();
            if (readData && blockers.Count == 0)
            {
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT " + string.Join(",", columns.Select(column =>
                {
                    var quoted = MySqlQuote(column.Name);
                    if (column.Kind == "integer") return quoted;
                    var length = "OCTET_LENGTH(" + quoted + ")";
                    return length + ",CASE WHEN " + length + "<=" + MaximumFieldBytes + " THEN CAST(" + quoted + " AS BINARY) ELSE NULL END";
                })) + " FROM " + MySqlQuote(name) + " LOCK IN SHARE MODE;";
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (++totalRows > MaximumRows) throw new InvalidOperationException("來源超過 100,000 列。");
                    var row = new object?[columns.Count];
                    var ordinal = 0;
                    for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                    {
                        var binary = columns[columnIndex].Kind != "integer";
                        var bytes = reader.IsDBNull(ordinal) ? 8 : binary ? Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture) : 8;
                        totalBytes += Math.Max(8, bytes);
                        if (bytes > MaximumFieldBytes || totalBytes > MaximumBytes) throw new InvalidOperationException("超過單欄 1 MiB／總量 64 MiB 預覽上限。");
                        if (binary) ordinal++;
                        object? value = reader.IsDBNull(ordinal) ? null : binary ? ReadBoundedBytes(reader, ordinal, bytes, token) : reader.GetInt64(ordinal);
                        if (value is byte[] encoded && columns[columnIndex].Kind == "text")
                        {
                            value = new UTF8Encoding(false, true).GetString(encoded);
                            if (((string)value).Contains('\0')) throw new InvalidOperationException("文字含 NUL，PostgreSQL 無法保存。");
                        }
                        ordinal++; row[columnIndex] = value;
                    }
                    rows.Add(row);
                }
            }
            tables.Add(new(name, columns, rows.Count, ContentHash(rows))
            {
                PrimaryKey = primary, PrimaryKeyName = primary.Count == 0 ? null : "PRIMARY",
                Indexes = indexes.Select(index => new LogicalMigrationIndex(index.Key, index.Value)).ToArray()
            });
            data.Add(name, rows);
        }
        return new(version, identity, tables, data, blockers, 0, packetBytes);
    }

    private static long MySqlBatchBytes(long packetBytes) => Math.Min(packetBytes / 2, 4 * 1024 * 1024);
    private static long MySqlRowBytes(object?[] row) => row.Sum(value => 64L + (value is byte[] binary ? 2L * binary.Length : value is string text ? 2L * Encoding.UTF8.GetByteCount(text) : 32));

    private static IEnumerable<object?[][]> MySqlBatches(List<object?[]> rows, long packetBytes)
    {
        var batch = new List<object?[]>();
        long bytes = 16384;
        foreach (var row in rows)
        {
            var size = MySqlRowBytes(row);
            if (batch.Count > 0 && (batch.Count == 100 || bytes + size > MySqlBatchBytes(packetBytes)))
            { yield return batch.ToArray(); batch.Clear(); bytes = 16384; }
            batch.Add(row); bytes += size;
        }
        if (batch.Count > 0) yield return batch.ToArray();
    }

    private static bool SafeMySqlDefinition(LogicalMigrationTable table, string sql, string collation)
    {
        var columns = table.Columns.Select(column => Regex.Escape(MySqlQuote(column.Name)) + "\\s+" +
            (column.Kind == "integer" ? "bigint(?:\\(20\\))?" : column.Kind == "binary" ? "longblob" :
                "longtext(?:\\s+CHARACTER\\s+SET\\s+utf8mb4)?(?:\\s+COLLATE\\s+" + collation + ")?") +
            (column.Nullable ? "(?:\\s+DEFAULT\\s+NULL)?" : "\\s+NOT\\s+NULL"));
        var key = table.PrimaryKey.Count == 0 ? "" : "\\s*,\\s*PRIMARY\\s+KEY\\s*\\(\\s*" + string.Join("\\s*,\\s*", table.PrimaryKey.Select(name => Regex.Escape(MySqlQuote(name)))) + "\\s*\\)";
        var indexes = table.Indexes.Select(index => "\\s*,\\s*KEY\\s+" + Regex.Escape(MySqlQuote(index.Name)) + "\\s*\\(\\s*" + string.Join("\\s*,\\s*", index.Columns.Select(name => Regex.Escape(MySqlQuote(name)))) + "\\s*\\)");
        return Regex.IsMatch(sql, "\\ACREATE\\s+TABLE\\s+" + Regex.Escape(MySqlQuote(table.Name)) + "\\s*\\(\\s*" +
            string.Join("\\s*,\\s*", columns) + key + string.Concat(indexes) + "\\s*\\)\\s+ENGINE=InnoDB\\s+DEFAULT\\s+CHARSET=utf8mb4\\s+COLLATE=" + collation + "\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static async Task<LogicalMigrationResult> RunMySqlTargetAsync(ConnectionProfile source, ConnectionProfile target,
        int selectedTargetMajor, LogicalMigrationPreview approved, CancellationToken token, Action<string, DbConnection, DbTransaction?>? observer)
    {
        using var sourceOwner = (AdoDatabaseSession)DatabaseProviderFactory.Create(source);
        using var targetOwner = (AdoDatabaseSession)DatabaseProviderFactory.Create(target);
        await using var origin = await sourceOwner.OpenMigrationConnectionAsync(source.Database, token, readOnly: true).ConfigureAwait(false);
        await using var destination = await targetOwner.OpenMigrationConnectionAsync(target.Database, token).ConfigureAwait(false);
        await using var sourceTransaction = await origin.BeginTransactionAsync(SourceIsolation(source.Provider), token).ConfigureAwait(false);
        var staging = new Dictionary<string, string>(StringComparer.Ordinal);
        var ddlAttempted = false;
        var publishAttempted = false;
        long rows = 0;
        var stage = "重新預檢";
        try
        {
            var snapshot = await ReadSnapshotAsync(origin, sourceTransaction, source.Provider, true, token).ConfigureAwait(false);
            var targetSnapshot = await ReadMySqlSnapshotAsync(destination, null, false, token).ConfigureAwait(false);
            var current = BuildPreview(source, target, selectedTargetMajor, snapshot, targetSnapshot);
            if (!current.CanStart || current.Fingerprint != approved.Fingerprint)
                return new(LogicalMigrationOutcome.Refused, 0, "來源／目標與確認快照不符；未寫入。");
            var prefix = "_mp_" + Guid.NewGuid().ToString("N") + "_";
            for (var index = 0; index < snapshot.Tables.Count; index++) staging.Add(prefix + index, snapshot.Tables[index].Name);
            stage = "建立本輪 staging（DDL 隱含提交）";
            foreach (var entry in staging)
            {
                token.ThrowIfCancellationRequested(); ddlAttempted = true;
                var table = snapshot.Tables.Single(table => table.Name == entry.Value) with { Name = entry.Key };
                await ExecuteAsync(destination, null, CreateMySqlTableSql(table, MySqlCollation(targetSnapshot.Version)), token).ConfigureAwait(false);
                await EnsureNoMySqlWarningsAsync(destination, null, token).ConfigureAwait(false);
                observer?.Invoke("staging", destination, null);
            }
            stage = "staging 分批 DML 交易";
            await using (var transaction = await destination.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false))
            {
                foreach (var entry in staging)
                {
                    var table = snapshot.Tables.Single(table => table.Name == entry.Value);
                    foreach (var batch in MySqlBatches(snapshot.Data[table.Name], targetSnapshot.PacketBytes))
                    {
                        token.ThrowIfCancellationRequested();
                        await using var command = destination.CreateCommand(); command.Transaction = transaction;
                        var values = new List<string>();
                        foreach (var row in batch)
                        {
                            var names = new List<string>();
                            for (var columnIndex = 0; columnIndex < row.Length; columnIndex++)
                            {
                                var parameter = command.CreateParameter(); parameter.ParameterName = "@p" + command.Parameters.Count;
                                parameter.DbType = table.Columns[columnIndex].Kind == "integer" ? DbType.Int64 : table.Columns[columnIndex].Kind == "text" ? DbType.String : DbType.Binary;
                                parameter.Value = row[columnIndex] ?? DBNull.Value;
                                names.Add(parameter.ParameterName); command.Parameters.Add(parameter);
                            }
                            values.Add("(" + string.Join(",", names) + ")");
                        }
                        command.CommandText = "INSERT INTO " + MySqlQuote(entry.Key) + " (" + string.Join(",", table.Columns.Select(column => MySqlQuote(column.Name))) + ") VALUES " + string.Join(",", values);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        await EnsureNoMySqlWarningsAsync(destination, transaction, token).ConfigureAwait(false);
                        rows += batch.Length;
                        observer?.Invoke("batch", destination, transaction);
                    }
                }
                stage = "staging 內容與結構校驗";
                observer?.Invoke("verify", destination, transaction);
                var written = await ReadMySqlSnapshotAsync(destination, transaction, true, token).ConfigureAwait(false);
                VerifyMySqlStaging(snapshot, written, staging);
                token.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            stage = "發布前確認來源與 staging";
            var finalSource = await ReadSnapshotAsync(origin, sourceTransaction, source.Provider, true, token).ConfigureAwait(false);
            if (finalSource.Blockers.Count != 0 || JsonSerializer.Serialize(finalSource.Tables) != JsonSerializer.Serialize(snapshot.Tables))
                throw new InvalidOperationException("來源在搬移期間改變。");
            await using (var transaction = await destination.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false))
            {
                VerifyMySqlStaging(snapshot, await ReadMySqlSnapshotAsync(destination, transaction, true, token).ConfigureAwait(false), staging);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            stage = "單一 multi-table RENAME 發布（非 ROLLBACK）";
            observer?.Invoke("publish", destination, null);
            token.ThrowIfCancellationRequested(); publishAttempted = true;
            await ExecuteAsync(destination, null, "RENAME TABLE " + string.Join(",", staging.Select(entry => MySqlQuote(entry.Key) + " TO " + MySqlQuote(entry.Value))) + ";", CancellationToken.None).ConfigureAwait(false);
            observer?.Invoke("published", destination, null);
            await using (var transaction = await destination.BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None).ConfigureAwait(false))
            {
                var published = await ReadMySqlSnapshotAsync(destination, transaction, true, CancellationToken.None).ConfigureAwait(false);
                VerifyMySqlStaging(snapshot, published, snapshot.Tables.ToDictionary(table => table.Name, table => table.Name));
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return new(LogicalMigrationOutcome.Verified, rows, "InnoDB staging 已驗證並以單一 RENAME TABLE 發布，發布後 schema／內容再次相符。DDL 不是可回滾交易；不自動重試。發布送出後不丟棄 Task，取消需等待實際結果。");
        }
        catch (Exception exception)
        {
            return new(publishAttempted ? LogicalMigrationOutcome.Uncertain : ddlAttempted ? LogicalMigrationOutcome.StagingRemains : LogicalMigrationOutcome.Refused,
                rows, "階段：" + stage + "；" + exception.GetType().Name + (token.IsCancellationRequested ? "；已取消" : "") +
                "；列數為處理進度，不是已提交列數；沒有刪除或覆蓋物件，沒有聲稱 DDL 回滾。" + (publishAttempted ? "發布結果不確定，禁止重試。" : "本輪 staging 可能殘留，禁止續寫／盲重試。") +
                "請 DBA 核對目標 " + target.Database + " 與本輪物件：" + string.Join(",", staging.Select(entry => entry.Key + " → " + entry.Value)));
        }
    }

    private static void VerifyMySqlStaging(Snapshot expected, Snapshot actual, IReadOnlyDictionary<string, string> staging)
    {
        if (actual.Blockers.Count != 0 || actual.Tables.Any(table => !staging.ContainsKey(table.Name)))
            throw new InvalidOperationException("staging catalog 有阻擋或出現其他物件。");
        var normalized = actual.Tables.Select(table => table with { Name = staging[table.Name] }).OrderBy(table => table.Name, StringComparer.Ordinal);
        if (JsonSerializer.Serialize(normalized) != JsonSerializer.Serialize(expected.Tables.Select(table => TargetTable(DatabaseProviderKind.MySql, table)).OrderBy(table => table.Name, StringComparer.Ordinal)))
            throw new InvalidOperationException("staging／發布目標 schema 或 SHA256 內容不符。");
    }

    private static async Task EnsureNoMySqlWarningsAsync(DbConnection connection, DbTransaction? transaction, CancellationToken token)
    {
        if (Convert.ToInt64(await ScalarAsync(connection, transaction, "SHOW COUNT(*) WARNINGS;", token)) != 0)
            throw new InvalidOperationException("MySQL 回報 warnings，不能把轉型／截斷當成功。");
    }
}
