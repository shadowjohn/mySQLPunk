using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using Npgsql;
using NpgsqlTypes;

namespace mySQLPunk.lib
{
    /// <summary>
    /// Windows 版資料比較與同步的 provider 包裝層：以既有 IDatabase 讀取資料列、找出計算與 identity 欄位、
    /// 依外鍵排序，並在正確的資料庫上以單一交易套用（MySQL 先 USE、SQL Server 暫時切換後切回、
    /// PostgreSQL 連線不在目標資料庫時拒絕）。只支援同類型資料庫。
    /// </summary>
    public static class DataSyncService
    {
        private const int PageSize = 1000;

        public static bool IsSupported(string sourceProvider, string targetProvider, out string reason)
        {
            string source = SchemaSyncScriptService.NormalizeProvider(sourceProvider);
            string target = SchemaSyncScriptService.NormalizeProvider(targetProvider);
            if (!string.Equals(source, target, StringComparison.Ordinal))
            {
                reason = Localization.T("DataSync.DifferentProviders");
                return false;
            }

            if (target != "mysql" && target != "postgresql" && target != "mssql" && target != "sqlite")
            {
                reason = Localization.Format("DataSync.UnsupportedProvider", target);
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static List<string> CommonTables(SchemaModelSnapshot source, SchemaModelSnapshot target)
        {
            HashSet<string> targetNames = new HashSet<string>(target.Tables.Select(table => table.Name), StringComparer.OrdinalIgnoreCase);
            return source.Tables.Select(table => table.Name)
                .Where(targetNames.Contains)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>被參照的表排在前面；循環相依維持原順序。</summary>
        public static List<string> OrderByDependencies(IList<string> tables, SchemaModelSnapshot targetSnapshot)
        {
            HashSet<string> selected = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> parents = tables.ToDictionary(
                table => table,
                table => new HashSet<string>(targetSnapshot.Relationships
                    .Where(item => string.Equals(item.FromTable, table, StringComparison.OrdinalIgnoreCase) &&
                                   selected.Contains(item.ToTable) &&
                                   !string.Equals(item.ToTable, table, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.ToTable), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            List<string> ordered = new List<string>();
            HashSet<string> placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (ordered.Count < tables.Count)
            {
                List<string> ready = tables.Where(table => !placed.Contains(table) && parents[table].All(placed.Contains)).ToList();
                if (ready.Count == 0) ready = tables.Where(table => !placed.Contains(table)).Take(1).ToList();
                foreach (string table in ready)
                {
                    ordered.Add(table);
                    placed.Add(table);
                }
            }
            return ordered;
        }

        public static DataTableComparison CompareTable(
            IDatabase source,
            string sourceDatabase,
            IDatabase target,
            string targetDatabase,
            SchemaModelSnapshot targetSnapshot,
            string tableName,
            int maximumRows)
        {
            string reason;
            if (!IsSupported(source.ProviderName, target.ProviderName, out reason))
            {
                return new DataTableComparison { TableName = tableName, SkippedReason = reason };
            }

            SchemaTableModel model = targetSnapshot.Tables.FirstOrDefault(table => string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase));
            List<string> keys = model == null
                ? new List<string>()
                : model.Columns.Where(column => column.IsPrimaryKey).OrderBy(column => column.Ordinal).Select(column => column.Name).ToList();
            DataTable sourceRows = LoadAll(source, sourceDatabase, tableName, maximumRows);
            if (sourceRows == null)
            {
                return new DataTableComparison { TableName = tableName, SkippedReason = Localization.Format("DataSync.Skip.TooLarge", maximumRows) };
            }

            DataTable targetRows = LoadAll(target, targetDatabase, tableName, maximumRows);
            if (targetRows == null)
            {
                return new DataTableComparison { TableName = tableName, SkippedReason = Localization.Format("DataSync.Skip.TooLarge", maximumRows) };
            }

            HashSet<string> computed = new HashSet<string>(GetComputedColumns(target, targetDatabase, tableName), StringComparer.OrdinalIgnoreCase);
            List<string> writable = targetRows.Columns.Cast<DataColumn>().Select(column => column.ColumnName).Where(name => !computed.Contains(name)).ToList();
            return DataSyncCore.Compare(tableName, sourceRows, targetRows, keys, writable);
        }

        public static string BuildPreview(IDatabase target, DataTableComparison comparison, bool includeDeletes)
        {
            string provider = SchemaSyncScriptService.NormalizeProvider(target.ProviderName);
            return DataSyncCore.BuildPreviewSql(comparison, name => Quote(provider, name), name => Qualify(provider, name), includeDeletes);
        }

        /// <summary>comparisons 需依 OrderByDependencies 的順序傳入。</summary>
        public static DataSyncResult Apply(IDatabase target, string targetDatabase, IList<DataTableComparison> comparisons, bool includeDeletes)
        {
            if (target == null) throw new ArgumentNullException("target");
            string provider = SchemaSyncScriptService.NormalizeProvider(target.ProviderName);
            List<DataSyncTableRequest> requests = new List<DataSyncTableRequest>();
            foreach (DataTableComparison comparison in comparisons.Where(item => !item.IsSkipped))
            {
                List<DataRowChange> changes = comparison.Changes.Where(change => includeDeletes || change.Kind != DataRowChangeKind.Delete).ToList();
                if (changes.Count == 0) continue;
                DataSyncTableRequest request = new DataSyncTableRequest
                {
                    TableName = comparison.TableName,
                    KeyColumns = comparison.KeyColumns,
                    Changes = changes,
                    AfterInsertStatements = new List<string>()
                };
                DataRowChange firstInsert = changes.FirstOrDefault(change => change.Kind == DataRowChangeKind.Insert);
                if (firstInsert != null)
                {
                    List<string> identity = GetIdentityColumns(target, targetDatabase, comparison.TableName)
                        .Where(column => firstInsert.Values.ContainsKey(column)).ToList();
                    if (identity.Count > 0 && provider == "mssql")
                    {
                        request.IdentityInsertOn = "SET IDENTITY_INSERT " + Qualify(provider, comparison.TableName) + " ON";
                        request.IdentityInsertOff = "SET IDENTITY_INSERT " + Qualify(provider, comparison.TableName) + " OFF";
                    }
                    else if (identity.Count > 0 && provider == "postgresql")
                    {
                        request.InsertClause = " OVERRIDING SYSTEM VALUE";
                        foreach (string column in identity)
                        {
                            // 明確寫入 identity 值不會推進序列；移到最大值之後，避免之後的預設新增撞主鍵。
                            request.AfterInsertStatements.Add(
                                "SELECT pg_catalog.setval(pg_catalog.pg_get_serial_sequence('" + Qualify(provider, comparison.TableName).Replace("'", "''") +
                                "', '" + column.Replace("'", "''") + "'), COALESCE(MAX(" + Quote(provider, column) + "), 1), MAX(" + Quote(provider, column) +
                                ") IS NOT NULL) FROM " + Qualify(provider, comparison.TableName));
                        }
                    }
                }
                requests.Add(request);
            }

            if (requests.Count == 0)
            {
                return new DataSyncResult { Succeeded = true };
            }

            return WithTargetConnection(target, targetDatabase, connection => DataSyncCore.Apply(
                connection,
                requests,
                name => Quote(provider, name),
                name => Qualify(provider, name),
                "@",
                ConfigureParameter));
        }

        private static void ConfigureParameter(DbParameter parameter, object value)
        {
            // Npgsql 會把 string 參數標成 text；未指定型別才能寫入 json／enum／inet 等欄位。
            NpgsqlParameter npgsql = parameter as NpgsqlParameter;
            if (npgsql != null && value is string) npgsql.NpgsqlDbType = NpgsqlDbType.Unknown;
        }

        private static T WithTargetConnection<T>(IDatabase database, string databaseName, Func<DbConnection, T> action)
        {
            my_mysql mysql = database as my_mysql;
            if (mysql != null)
            {
                if (mysql.MCT.State != ConnectionState.Open) mysql.MCT.Open();
                using (DbCommand use = mysql.MCT.CreateCommand())
                {
                    use.CommandText = "USE `" + (databaseName ?? string.Empty).Replace("`", "``") + "`";
                    use.ExecuteNonQuery();
                }
                return action(mysql.MCT);
            }

            my_mssql sqlServer = database as my_mssql;
            if (sqlServer != null)
            {
                if (sqlServer.MCT.State != ConnectionState.Open) sqlServer.MCT.Open();
                string original = sqlServer.MCT.Database;
                try
                {
                    sqlServer.MCT.ChangeDatabase(databaseName);
                    return action(sqlServer.MCT);
                }
                finally
                {
                    if (!string.IsNullOrEmpty(original) && sqlServer.MCT.State == ConnectionState.Open) sqlServer.MCT.ChangeDatabase(original);
                }
            }

            my_postgresql postgres = database as my_postgresql;
            if (postgres != null)
            {
                if (postgres.MCT.State != ConnectionState.Open) postgres.MCT.Open();
                if (!string.Equals(postgres.MCT.Database, databaseName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(Localization.Format("SchemaSync.Exec.WrongDatabase", postgres.MCT.Database, databaseName));
                }
                return action(postgres.MCT);
            }

            my_sqlite sqlite = database as my_sqlite;
            if (sqlite != null)
            {
                if (sqlite.MCT.State != ConnectionState.Open) sqlite.MCT.Open();
                return action(sqlite.MCT);
            }

            throw new NotSupportedException(Localization.Format("DataSync.UnsupportedProvider", database.ProviderName));
        }

        private static DataTable LoadAll(IDatabase database, string databaseName, string tableName, int maximumRows)
        {
            DataTable all = null;
            long offset = 0;
            while (true)
            {
                DataTable page = database.SelectTablePage(databaseName, tableName, offset, PageSize);
                ThrowIfQueryFailed(page);
                if (all == null) all = page.Clone();
                foreach (DataRow row in page.Rows) all.ImportRow(row);
                if (all.Rows.Count > maximumRows) return null;
                if (page.Rows.Count < PageSize) return all;
                offset += page.Rows.Count;
            }
        }

        private static void ThrowIfQueryFailed(DataTable table)
        {
            if (table == null) throw new InvalidOperationException(Localization.T("DataSync.Error.ReadFailed"));
            if (table.ExtendedProperties.ContainsKey(my_sqlite.QueryErrorExtendedProperty))
            {
                throw new InvalidOperationException(Convert.ToString(table.ExtendedProperties[my_sqlite.QueryErrorExtendedProperty]));
            }
        }

        private static IEnumerable<string> GetComputedColumns(IDatabase database, string databaseName, string tableName)
        {
            string provider = SchemaSyncScriptService.NormalizeProvider(database.ProviderName);
            SplitName(provider, tableName, out string schema, out string table);
            DataTable rows;
            switch (provider)
            {
                case "mysql":
                    rows = database.SelectSQL(
                        "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = ?db AND TABLE_NAME = ?tableName AND (EXTRA LIKE '%GENERATED%')",
                        new Dictionary<string, object> { { "db", databaseName }, { "tableName", table } });
                    break;
                case "postgresql":
                    rows = database.SelectSQL(
                        "SELECT column_name FROM information_schema.columns WHERE table_schema = :schema AND table_name = :tableName AND is_generated = 'ALWAYS'",
                        new Dictionary<string, object> { { "schema", schema }, { "tableName", table } });
                    break;
                case "mssql":
                    rows = database.SelectSQL(
                        "SELECT c.name FROM " + Quote(provider, databaseName) + ".sys.columns c JOIN " + Quote(provider, databaseName) + ".sys.types t ON t.user_type_id = c.user_type_id " +
                        "WHERE c.object_id = OBJECT_ID(@name) AND (c.is_computed = 1 OR t.name IN ('timestamp', 'rowversion'))",
                        new Dictionary<string, object> { { "name", Quote(provider, databaseName) + "." + Quote(provider, schema) + "." + Quote(provider, table) } });
                    break;
                case "sqlite":
                    rows = database.SelectSQL("SELECT name FROM pragma_table_xinfo(@tableName) WHERE hidden IN (2, 3)", new Dictionary<string, object> { { "tableName", table } });
                    break;
                default:
                    return Enumerable.Empty<string>();
            }
            ThrowIfQueryFailed(rows);
            return rows.Rows.Cast<DataRow>().Select(row => Convert.ToString(row[0])).ToList();
        }

        private static IEnumerable<string> GetIdentityColumns(IDatabase database, string databaseName, string tableName)
        {
            string provider = SchemaSyncScriptService.NormalizeProvider(database.ProviderName);
            SplitName(provider, tableName, out string schema, out string table);
            DataTable rows;
            switch (provider)
            {
                case "postgresql":
                    rows = database.SelectSQL(
                        "SELECT column_name FROM information_schema.columns WHERE table_schema = :schema AND table_name = :tableName AND is_identity = 'YES'",
                        new Dictionary<string, object> { { "schema", schema }, { "tableName", table } });
                    break;
                case "mssql":
                    rows = database.SelectSQL(
                        "SELECT c.name FROM " + Quote(provider, databaseName) + ".sys.columns c WHERE c.object_id = OBJECT_ID(@name) AND c.is_identity = 1",
                        new Dictionary<string, object> { { "name", Quote(provider, databaseName) + "." + Quote(provider, schema) + "." + Quote(provider, table) } });
                    break;
                default:
                    return Enumerable.Empty<string>();
            }
            ThrowIfQueryFailed(rows);
            return rows.Rows.Cast<DataRow>().Select(row => Convert.ToString(row[0])).ToList();
        }

        private static void SplitName(string provider, string name, out string schema, out string table)
        {
            string value = name ?? string.Empty;
            int dot = value.IndexOf('.');
            if ((provider == "postgresql" || provider == "mssql") && dot > 0 && dot < value.Length - 1)
            {
                schema = value.Substring(0, dot);
                table = value.Substring(dot + 1);
                return;
            }
            schema = provider == "mssql" ? "dbo" : provider == "postgresql" ? "public" : string.Empty;
            table = value;
        }

        private static string Qualify(string provider, string name)
        {
            SplitName(provider, name, out string schema, out string table);
            return string.IsNullOrEmpty(schema) ? Quote(provider, table) : Quote(provider, schema) + "." + Quote(provider, table);
        }

        private static string Quote(string provider, string identifier)
        {
            string value = identifier ?? string.Empty;
            if (provider == "mysql") return "`" + value.Replace("`", "``") + "`";
            if (provider == "mssql") return "[" + value.Replace("]", "]]") + "]";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
