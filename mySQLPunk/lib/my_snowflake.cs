using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace mySQLPunk.lib
{
    /// <summary>
    /// Snowflake provider：SQL REST API v2 直連（PAT／OAuth bearer），
    /// 提供 database／schema.table metadata、查詢，以及查詢編輯器的單一 DML／DDL 執行。
    /// 不引入官方 .NET 驅動：5.x 已退出 net472，4.x 會帶進 Arrow／AWS／Azure／GCS 相依樹。
    /// </summary>
    public sealed class my_snowflake : IDatabase
    {
        private const int DefaultPageLimit = 1000;

        /// <summary>會回傳結果集的唯讀 statement 開頭。</summary>
        private static readonly Regex ReadOnlyStatement = new Regex(
            @"^\s*(SELECT|SHOW|DESC|DESCRIBE|EXPLAIN|WITH)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly object _sync = new object();
        private string account = string.Empty;
        private string username = string.Empty;
        private string token = string.Empty;
        private string tokenType = "PROGRAMMATIC_ACCESS_TOKEN";
        private string defaultDatabase = string.Empty;
        private string defaultSchema = string.Empty;
        private string warehouse = string.Empty;
        private string role = string.Empty;
        private string endpointOverride = string.Empty;
        private SnowflakeRestClient client;
        private bool open;

        public string ProviderName => "snowflake";
        public ConnectionState State => open ? ConnectionState.Open : ConnectionState.Closed;

        public void SetConn(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Localization.T("Snowflake.ConnectionStringRequired"), "value");
            Uri uri;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) ||
                !string.Equals(uri.Scheme, "snowflake", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(Localization.T("Snowflake.ConnectionStringInvalid"), "value");

            account = uri.Host;
            if (string.IsNullOrWhiteSpace(account))
                throw new ArgumentException(Localization.T("Snowflake.AccountRequired"), "value");
            username = string.Empty;
            token = string.Empty;
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                int split = uri.UserInfo.IndexOf(':');
                if (split >= 0)
                {
                    username = Uri.UnescapeDataString(uri.UserInfo.Substring(0, split));
                    token = Uri.UnescapeDataString(uri.UserInfo.Substring(split + 1));
                }
                else
                {
                    token = Uri.UnescapeDataString(uri.UserInfo);
                }
            }
            defaultDatabase = Uri.UnescapeDataString((uri.AbsolutePath ?? string.Empty).Trim('/'));

            defaultSchema = string.Empty;
            warehouse = string.Empty;
            role = string.Empty;
            tokenType = "PROGRAMMATIC_ACCESS_TOKEN";
            endpointOverride = string.Empty;
            foreach (string pair in (uri.Query ?? string.Empty).TrimStart('?')
                .Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string name = eq < 0 ? pair : pair.Substring(0, eq);
                string parameterValue = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(eq + 1));
                switch (name.ToLowerInvariant())
                {
                    case "schema": defaultSchema = parameterValue; break;
                    case "warehouse": warehouse = parameterValue; break;
                    case "role": role = parameterValue; break;
                    case "auth":
                        if (string.Equals(parameterValue, "oauth", StringComparison.OrdinalIgnoreCase)) tokenType = "OAUTH";
                        else if (string.Equals(parameterValue, "pat", StringComparison.OrdinalIgnoreCase)) tokenType = "PROGRAMMATIC_ACCESS_TOKEN";
                        else throw new ArgumentException(Localization.Format("Snowflake.UnsupportedAuthType", parameterValue), "value");
                        break;
                    case "endpoint":
                        // 僅供 loopback 測試替換端點；正式連線一律走官方網域。
                        if (!IsLoopbackEndpoint(parameterValue))
                            throw new ArgumentException(Localization.T("Snowflake.InvalidEndpoint"), "value");
                        endpointOverride = parameterValue.TrimEnd('/');
                        break;
                    default:
                        throw new ArgumentException(Localization.Format("Snowflake.UnsupportedParameter", name), "value");
                }
            }
        }

        public static string BuildConnectionString(string account, string username, string token, string database,
            string schema, string warehouse, string role, bool useOAuth, string endpoint = null)
        {
            string auth = string.IsNullOrEmpty(username)
                ? ":" + Uri.EscapeDataString(token ?? string.Empty) + "@"
                : Uri.EscapeDataString(username) + ":" + Uri.EscapeDataString(token ?? string.Empty) + "@";
            List<string> query = new List<string>();
            if (!string.IsNullOrWhiteSpace(schema)) query.Add("schema=" + Uri.EscapeDataString(schema.Trim()));
            if (!string.IsNullOrWhiteSpace(warehouse)) query.Add("warehouse=" + Uri.EscapeDataString(warehouse.Trim()));
            if (!string.IsNullOrWhiteSpace(role)) query.Add("role=" + Uri.EscapeDataString(role.Trim()));
            if (useOAuth) query.Add("auth=oauth");
            if (!string.IsNullOrWhiteSpace(endpoint)) query.Add("endpoint=" + Uri.EscapeDataString(endpoint.Trim()));
            return "snowflake://" + auth + (account ?? string.Empty).Trim()
                + "/" + Uri.EscapeDataString((database ?? string.Empty).Trim())
                + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        }

        public void Open()
        {
            if (string.IsNullOrWhiteSpace(account)) throw new InvalidOperationException(Localization.T("Snowflake.ConnectionStringRequired"));
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException(Localization.T("Snowflake.TokenRequired"));
            lock (_sync)
            {
                SnowflakeRestClient candidate = new SnowflakeRestClient(BuildBaseUrl(), token, tokenType);
                try
                {
                    client = candidate;
                    Execute("SELECT CURRENT_VERSION()");
                    open = true;
                }
                catch
                {
                    client = null;
                    candidate.Dispose();
                    throw;
                }
            }
        }

        public void Close()
        {
            lock (_sync)
            {
                if (client != null) client.Dispose();
                client = null;
                open = false;
            }
        }

        public void Dispose()
        {
            Close();
        }

        public List<string> GetDatabases()
        {
            SnowflakeStatementResult result = ExecuteLocked("SHOW DATABASES");
            int nameIndex = result.ColumnNames.FindIndex(n => string.Equals(n, "name", StringComparison.OrdinalIgnoreCase));
            List<string> databases = new List<string>();
            if (nameIndex < 0) return databases;
            foreach (object[] row in result.Rows)
            {
                string name = Convert.ToString(row[nameIndex], CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(name)) databases.Add(name);
            }
            return databases;
        }

        public List<string> GetTables(string databaseName)
        {
            return ListObjects(databaseName, "BASE TABLE");
        }

        public List<string> GetViews(string databaseName)
        {
            return ListObjects(databaseName, "VIEW");
        }

        public DataTable GetColumns(string databaseName, string tableName)
        {
            string schemaName, objectName;
            SplitSchemaObject(tableName, out schemaName, out objectName);
            SnowflakeStatementResult result = ExecuteLocked(
                "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, COMMENT, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE"
                + " FROM " + QuoteIdentifier(databaseName) + ".INFORMATION_SCHEMA.COLUMNS"
                + " WHERE TABLE_SCHEMA = '" + EscapeLiteral(schemaName) + "' AND TABLE_NAME = '" + EscapeLiteral(objectName) + "'"
                + " ORDER BY ORDINAL_POSITION");
            DataTable table = new DataTable();
            table.Columns.Add("Field");
            table.Columns.Add("Type");
            table.Columns.Add("Null");
            table.Columns.Add("Key");
            table.Columns.Add("Default");
            table.Columns.Add("Extra");
            table.Columns.Add("Comment");
            foreach (object[] row in result.Rows)
            {
                DataRow output = table.NewRow();
                output["Field"] = Convert.ToString(row[0], CultureInfo.InvariantCulture);
                output["Type"] = DescribeType(row);
                output["Null"] = string.Equals(Convert.ToString(row[2], CultureInfo.InvariantCulture), "YES", StringComparison.OrdinalIgnoreCase) ? "YES" : "NO";
                output["Key"] = string.Empty;
                output["Default"] = row[3] == DBNull.Value ? string.Empty : Convert.ToString(row[3], CultureInfo.InvariantCulture);
                output["Extra"] = string.Empty;
                output["Comment"] = row[4] == DBNull.Value ? string.Empty : Convert.ToString(row[4], CultureInfo.InvariantCulture);
                table.Rows.Add(output);
            }
            return table;
        }

        public DataTable GetIndexes(string databaseName, string tableName)
        {
            // Snowflake 採 micro-partition，無傳統索引物件。
            DataTable result = new DataTable();
            result.Columns.Add("Key_name");
            result.Columns.Add("Column_name");
            result.Columns.Add("Non_unique", typeof(int));
            result.Columns.Add("Seq_in_index", typeof(int));
            result.Columns.Add("Index_type");
            return result;
        }

        public DataTable GetTableStatus(string databaseName)
        {
            SnowflakeStatementResult result = ExecuteLocked(
                "SELECT TABLE_SCHEMA || '.' || TABLE_NAME, ROW_COUNT, BYTES, COMMENT"
                + " FROM " + QuoteIdentifier(databaseName) + ".INFORMATION_SCHEMA.TABLES"
                + " WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_SCHEMA <> 'INFORMATION_SCHEMA'"
                + " ORDER BY 1");
            DataTable table = new DataTable();
            table.Columns.Add("Name");
            table.Columns.Add("Rows", typeof(long));
            table.Columns.Add("Data_length", typeof(long));
            table.Columns.Add("Index_length", typeof(long));
            table.Columns.Add("Engine");
            table.Columns.Add("Update_time");
            table.Columns.Add("Comment");
            foreach (object[] row in result.Rows)
            {
                DataRow output = table.NewRow();
                output["Name"] = Convert.ToString(row[0], CultureInfo.InvariantCulture);
                output["Rows"] = ParseLong(row[1]);
                output["Data_length"] = ParseLong(row[2]);
                output["Index_length"] = 0L;
                output["Engine"] = "Snowflake";
                output["Update_time"] = string.Empty;
                output["Comment"] = row[3] == DBNull.Value ? string.Empty : Convert.ToString(row[3], CultureInfo.InvariantCulture);
                table.Rows.Add(output);
            }
            return table;
        }

        public Dictionary<string, string> GetDatabaseInfo(string databaseName)
        {
            SnowflakeStatementResult result = ExecuteLocked(
                "SELECT CURRENT_VERSION(), CURRENT_REGION(), CURRENT_WAREHOUSE(), CURRENT_ROLE(), CURRENT_USER()");
            Dictionary<string, string> info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Provider", "Snowflake" },
                { "Database", databaseName ?? string.Empty }
            };
            if (result.Rows.Count > 0)
            {
                object[] row = result.Rows[0];
                info["version"] = Convert.ToString(row[0], CultureInfo.InvariantCulture);
                info["region"] = Convert.ToString(row[1], CultureInfo.InvariantCulture);
                info["warehouse"] = row[2] == DBNull.Value ? string.Empty : Convert.ToString(row[2], CultureInfo.InvariantCulture);
                info["role"] = Convert.ToString(row[3], CultureInfo.InvariantCulture);
                info["user"] = Convert.ToString(row[4], CultureInfo.InvariantCulture);
            }
            return info;
        }

        public string GetTableCreateStatement(string databaseName, string tableName)
        {
            string schemaName, objectName;
            SplitSchemaObject(tableName, out schemaName, out objectName);
            string qualified = QuoteIdentifier(databaseName) + "." + QuoteIdentifier(schemaName) + "." + QuoteIdentifier(objectName);
            try
            {
                SnowflakeStatementResult result = ExecuteLocked(
                    "SELECT GET_DDL('TABLE', '" + EscapeLiteral(qualified) + "')");
                return result.Rows.Count > 0 ? Convert.ToString(result.Rows[0][0], CultureInfo.InvariantCulture) : string.Empty;
            }
            catch (SnowflakeServerException)
            {
                return string.Empty;
            }
        }

        public bool TableExists(string databaseName, string tableName)
        {
            return ObjectExists(databaseName, tableName, "BASE TABLE");
        }

        public bool ViewExists(string databaseName, string viewName)
        {
            return ObjectExists(databaseName, viewName, "VIEW");
        }

        public long CountRows(string databaseName, string tableName)
        {
            string schemaName, objectName;
            SplitSchemaObject(tableName, out schemaName, out objectName);
            SnowflakeStatementResult result = ExecuteLocked(
                "SELECT COUNT(*) FROM " + QuoteIdentifier(databaseName) + "." + QuoteIdentifier(schemaName) + "." + QuoteIdentifier(objectName));
            return result.Rows.Count > 0 ? ParseLong(result.Rows[0][0]) : 0L;
        }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            string schemaName, objectName;
            SplitSchemaObject(tableName, out schemaName, out objectName);
            if (limit <= 0) limit = DefaultPageLimit;
            SnowflakeStatementResult result = ExecuteLocked(
                "SELECT * FROM " + QuoteIdentifier(databaseName) + "." + QuoteIdentifier(schemaName) + "." + QuoteIdentifier(objectName)
                + " LIMIT " + limit.ToString(CultureInfo.InvariantCulture)
                + " OFFSET " + Math.Max(0L, offset).ToString(CultureInfo.InvariantCulture));
            return BuildDataTable(result);
        }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException(Localization.T("Snowflake.QueryRequired"), "sql");
            if (parameters != null && parameters.Count > 0)
                throw new NotSupportedException(Localization.T("Snowflake.ParametersUnsupported"));
            if (!ReadOnlyStatement.IsMatch(sql))
                throw new NotSupportedException(Localization.T("Snowflake.UseExecuteForWrite"));
            return BuildDataTable(ExecuteLocked(sql));
        }

        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            return Task.Run(() => SelectSQL(sql, parameters));
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            Dictionary<string, string> output = new Dictionary<string, string>();
            try
            {
                if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException(Localization.T("Snowflake.QueryRequired"), "sql");
                if (parameters != null && parameters.Count > 0)
                    throw new NotSupportedException(Localization.T("Snowflake.ParametersUnsupported"));

                SnowflakeStatementResult result = ExecuteLocked(sql);
                output["status"] = "OK";
                output["rowsAffected"] = ExtractAffectedRows(result).ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                output["status"] = "NO";
                output["reason"] = ExceptionMessageService.GetReason(ex);
            }
            return output;
        }

        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            return Task.Run(() => ExecSQL(sql, parameters));
        }

        public DataTable GetCopyColumns(string databaseName, string tableName) { throw UnsupportedWrite(); }
        public DataTable GetCopyIndexes(string databaseName, string tableName) { throw UnsupportedWrite(); }
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { throw UnsupportedWrite(); }
        public void DropTableForCopy(string databaseName, string tableName) { throw UnsupportedWrite(); }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { throw UnsupportedWrite(); }
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { throw UnsupportedWrite(); }
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { throw UnsupportedWrite(); }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { throw UnsupportedWrite(); }
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { throw UnsupportedWrite(); }

        public string GetViewCreateStatement(string databaseName, string viewName)
        {
            string schemaName, objectName;
            SplitSchemaObject(viewName, out schemaName, out objectName);
            string qualified = QuoteIdentifier(databaseName) + "." + QuoteIdentifier(schemaName) + "." + QuoteIdentifier(objectName);
            try
            {
                SnowflakeStatementResult result = ExecuteLocked(
                    "SELECT GET_DDL('VIEW', '" + EscapeLiteral(qualified) + "')");
                return result.Rows.Count > 0 ? Convert.ToString(result.Rows[0][0], CultureInfo.InvariantCulture) : string.Empty;
            }
            catch (SnowflakeServerException)
            {
                return string.Empty;
            }
        }

        /// <summary>把 schema.table 節點名稱拆成 schema 與物件名；只切第一個點，物件名內的點不支援。</summary>
        public static void SplitSchemaObject(string value, out string schemaName, out string objectName)
        {
            string trimmed = (value ?? string.Empty).Trim();
            int dot = trimmed.IndexOf('.');
            if (dot <= 0 || dot >= trimmed.Length - 1)
                throw new ArgumentException(Localization.Format("Snowflake.InvalidObjectName", value ?? string.Empty), "value");
            schemaName = trimmed.Substring(0, dot);
            objectName = trimmed.Substring(dot + 1);
        }

        public static string QuoteIdentifier(string name)
        {
            return "\"" + (name ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        public static string EscapeLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        /// <summary>
        /// Snowflake 的 DML ResultSet 會以「number of rows inserted／updated／deleted」欄位回傳計數；
        /// MERGE 可能同時含多欄，因此加總所有符合欄位。DDL 沒有這類欄位時回傳 0。
        /// </summary>
        public static long ExtractAffectedRows(SnowflakeStatementResult result)
        {
            if (result == null || result.Rows.Count == 0) return 0L;
            long total = 0L;
            object[] row = result.Rows[0];
            for (int i = 0; i < result.ColumnNames.Count && i < row.Length; i++)
            {
                string name = (result.ColumnNames[i] ?? string.Empty).Trim().ToLowerInvariant();
                if (!name.StartsWith("number of rows ", StringComparison.Ordinal) ||
                    !(name.EndsWith(" inserted", StringComparison.Ordinal) ||
                      name.EndsWith(" updated", StringComparison.Ordinal) ||
                      name.EndsWith(" deleted", StringComparison.Ordinal) ||
                      name.EndsWith(" affected", StringComparison.Ordinal)))
                    continue;

                long count;
                if (long.TryParse(Convert.ToString(row[i], CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out count)) total += count;
            }
            return total;
        }

        private static bool IsLoopbackEndpoint(string value)
        {
            Uri uri;
            if (!Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out uri)) return false;
            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)) return false;
            return uri.IsLoopback;
        }

        private string BuildBaseUrl()
        {
            if (!string.IsNullOrEmpty(endpointOverride)) return endpointOverride;
            string host = account.Trim();
            if (host.IndexOf(".snowflakecomputing.com", StringComparison.OrdinalIgnoreCase) < 0)
                host += ".snowflakecomputing.com";
            return "https://" + host;
        }

        private List<string> ListObjects(string databaseName, string tableType)
        {
            SnowflakeStatementResult result = ExecuteLocked(
                "SELECT TABLE_SCHEMA || '.' || TABLE_NAME"
                + " FROM " + QuoteIdentifier(databaseName) + ".INFORMATION_SCHEMA.TABLES"
                + " WHERE TABLE_TYPE = '" + EscapeLiteral(tableType) + "' AND TABLE_SCHEMA <> 'INFORMATION_SCHEMA'"
                + " ORDER BY 1");
            List<string> names = new List<string>();
            foreach (object[] row in result.Rows)
                names.Add(Convert.ToString(row[0], CultureInfo.InvariantCulture));
            return names;
        }

        private bool ObjectExists(string databaseName, string objectFullName, string tableType)
        {
            string schemaName, objectName;
            try { SplitSchemaObject(objectFullName, out schemaName, out objectName); }
            catch (ArgumentException) { return false; }
            SnowflakeStatementResult result = ExecuteLocked(
                "SELECT COUNT(*) FROM " + QuoteIdentifier(databaseName) + ".INFORMATION_SCHEMA.TABLES"
                + " WHERE TABLE_TYPE = '" + EscapeLiteral(tableType) + "'"
                + " AND TABLE_SCHEMA = '" + EscapeLiteral(schemaName) + "' AND TABLE_NAME = '" + EscapeLiteral(objectName) + "'");
            return result.Rows.Count > 0 && ParseLong(result.Rows[0][0]) > 0;
        }

        private static DataTable BuildDataTable(SnowflakeStatementResult result)
        {
            DataTable table = new DataTable();
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in result.ColumnNames)
            {
                string columnName = string.IsNullOrWhiteSpace(name) ? "column" : name;
                string candidate = columnName;
                int suffix = 1;
                while (!used.Add(candidate)) candidate = columnName + "_" + suffix++;
                table.Columns.Add(candidate, typeof(string));
            }
            foreach (object[] row in result.Rows)
            {
                DataRow output = table.NewRow();
                for (int i = 0; i < table.Columns.Count && i < row.Length; i++) output[i] = row[i];
                table.Rows.Add(output);
            }
            return table;
        }

        private static long ParseLong(object value)
        {
            long parsed;
            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : 0L;
        }

        private static string DescribeType(object[] columnRow)
        {
            string dataType = Convert.ToString(columnRow[1], CultureInfo.InvariantCulture);
            string length = Convert.ToString(columnRow[5], CultureInfo.InvariantCulture);
            string precision = Convert.ToString(columnRow[6], CultureInfo.InvariantCulture);
            string scale = Convert.ToString(columnRow[7], CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(length) && columnRow[5] != DBNull.Value) return dataType + "(" + length + ")";
            if (!string.IsNullOrEmpty(precision) && columnRow[6] != DBNull.Value)
                return dataType + "(" + precision + (string.IsNullOrEmpty(scale) || columnRow[7] == DBNull.Value ? string.Empty : "," + scale) + ")";
            return dataType;
        }

        private SnowflakeStatementResult ExecuteLocked(string sql)
        {
            lock (_sync)
            {
                return Execute(sql);
            }
        }

        private SnowflakeStatementResult Execute(string sql)
        {
            if (client == null) throw new InvalidOperationException(Localization.T("Snowflake.ConnectionNotOpen"));
            return client.ExecuteStatement(sql, defaultDatabase, defaultSchema, warehouse, role);
        }

        private static Exception UnsupportedWrite()
        {
            return new NotSupportedException(Localization.T("Snowflake.StructuredWriteUnsupported"));
        }
    }
}
