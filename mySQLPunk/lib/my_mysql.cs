using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MySqlConnector;
using System.Data;
using utility;
namespace mySQLPunk.lib
{
    public class my_mysql : IDatabase
    {
        myinclude my = new myinclude();
        public MySqlConnection MCT = null;
        public MySqlCommand MC = null;
        public MySqlParameter PA = null;

        public ConnectionState State => MCT?.State ?? ConnectionState.Closed;
        public string ProviderName => "mysql";

        private const int MetadataCommandTimeoutSeconds = 8;
        private const uint DefaultConnectionTimeoutSeconds = 8;

        public void SetConn(string connection)
        {
            if (!connection.ToLower().Contains("allowzerodatetime"))
            {
                if (!connection.EndsWith(";")) connection += ";";
                connection += "AllowZeroDateTime=True;";
            }
            connection = EnsureMySqlConnectionTimeout(connection);
            MCT = new MySqlConnection(connection);
        }
        public void setConn(string connection) => SetConn(connection);

        private static bool HasConnectionTimeoutSetting(string connection)
        {
            if (string.IsNullOrWhiteSpace(connection)) return false;
            return connection.IndexOf("connection timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   connection.IndexOf("connectiontimeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   connection.IndexOf("connect timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   connection.IndexOf("connecttimeout", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string EnsureMySqlConnectionTimeout(string connection)
        {
            if (string.IsNullOrWhiteSpace(connection)) return connection;
            if (HasConnectionTimeoutSetting(connection)) return connection;

            try
            {
                var builder = new MySqlConnectionStringBuilder(connection)
                {
                    ConnectionTimeout = DefaultConnectionTimeoutSeconds
                };
                return builder.ConnectionString;
            }
            catch
            {
                return connection;
            }
        }

        private DataTable ExecuteDataTable(string sql, Dictionary<string, object> parameters, int commandTimeoutSeconds)
        {
            DataTable output = new DataTable();
            using (MySqlCommand cmd = new MySqlCommand(sql, MCT))
            {
                cmd.CommandTimeout = commandTimeoutSeconds;
                if (parameters != null)
                {
                    foreach (var key in parameters.Keys)
                    {
                        cmd.Parameters.Add(new MySqlParameter("?" + key, parameters[key]));
                    }
                }
                using (MySqlDataReader reader = cmd.ExecuteReader())
                {
                    output.Load(reader);
                }
            }
            return output;
        }

        public void setTimeout(int timeout)
        {
            if (MC != null) MC.CommandTimeout = timeout;
        }

        public void Open()
        {
            if (MCT.State != ConnectionState.Open) MCT.Open();
        }
        public void open() => Open();

        public void Close()
        {
            if (MCT.State != ConnectionState.Closed) MCT.Close();
        }
        public void close() => Close();

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            return selectSQL_SAFE(sql, parameters ?? new Dictionary<string, object>());
        }

        public DataTable selectSQL_SAFE(string SQL)
        {
            return selectSQL_SAFE(SQL, new Dictionary<string, object>());
        }

        public DataTable selectSQL_SAFE(string SQL, Dictionary<string, object> key_value)
        {
            DataTable output = new DataTable();
            using (MySqlCommand cmd = new MySqlCommand(SQL, MCT))
            {
                foreach (var key in key_value.Keys)
                {
                    cmd.Parameters.Add(new MySqlParameter("?" + key, key_value[key]));
                }
                using (MySqlDataReader reader = cmd.ExecuteReader())
                {
                    output.Load(reader);
                }
            }
            return output;
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            return execSQL_SAFE(sql, parameters ?? new Dictionary<string, object>());
        }

        public Dictionary<string, string> execSQL_SAFE(string SQL, Dictionary<string, object> m)
        {
            Dictionary<string, string> output = new Dictionary<string, string>();
            try
            {
                using (MySqlCommand cmd = new MySqlCommand(SQL, MCT))
                {
                    foreach (var key in m.Keys)
                    {
                        cmd.Parameters.Add(new MySqlParameter("?" + key, m[key]));
                    }
                    output["rowsAffected"] = cmd.ExecuteNonQuery().ToString();
                }
                output["status"] = "OK";
                return output;
            }
            catch (Exception ex)
            {
                output["status"] = "NO";
                output["reason"] = ex.Message;
                return output;
            }
        }

        public async System.Threading.Tasks.Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            DataTable output = new DataTable();
            using (MySqlCommand cmd = new MySqlCommand(sql, MCT))
            {
                if (parameters != null)
                {
                    foreach (var key in parameters.Keys)
                    {
                        cmd.Parameters.Add(new MySqlParameter("?" + key, parameters[key]));
                    }
                }
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    output.Load(reader);
                }
            }
            return output;
        }

        public async System.Threading.Tasks.Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            Dictionary<string, string> output = new Dictionary<string, string>();
            try
            {
                using (MySqlCommand cmd = new MySqlCommand(sql, MCT))
                {
                    if (parameters != null)
                    {
                        foreach (var key in parameters.Keys)
                        {
                            cmd.Parameters.Add(new MySqlParameter("?" + key, parameters[key]));
                        }
                    }
                    output["rowsAffected"] = (await cmd.ExecuteNonQueryAsync()).ToString();
                }
                output["status"] = "OK";
                return output;
            }
            catch (Exception ex)
            {
                output["status"] = "NO";
                output["reason"] = ex.Message;
                return output;
            }
        }

        public List<string> GetDatabases()
        {
            List<string> dbs = new List<string>();
            DataTable dt = ExecuteDataTable("SHOW DATABASES;", null, MetadataCommandTimeoutSeconds);
            foreach (DataRow row in dt.Rows)
            {
                dbs.Add(row[0].ToString());
            }
            return dbs;
        }

        public List<string> GetTables(string databaseName)
        {
            string safeDB = databaseName.Replace("`", "``");
            List<string> tables = new List<string>();
            DataTable dt = ExecuteDataTable(
                $"SHOW FULL TABLES FROM `{safeDB}` WHERE Table_type = 'BASE TABLE';",
                null,
                MetadataCommandTimeoutSeconds);
            foreach (DataRow row in dt.Rows)
            {
                tables.Add(row[0].ToString());
            }
            return tables;
        }

        public List<string> GetViews(string databaseName)
        {
            string safeDB = databaseName.Replace("`", "``");
            List<string> views = new List<string>();
            DataTable dt = ExecuteDataTable(
                $"SHOW FULL TABLES FROM `{safeDB}` WHERE Table_type = 'VIEW';",
                null,
                MetadataCommandTimeoutSeconds);
            foreach (DataRow row in dt.Rows)
            {
                views.Add(row[0].ToString());
            }
            return views;
        }

        public DataTable GetColumns(string databaseName, string tableName)
        {
            string safeDB = databaseName.Replace("`", "``");
            string safeTable = tableName.Replace("`", "``");
            return ExecuteDataTable(
                $"SHOW FULL COLUMNS FROM `{safeDB}`.`{safeTable}`;",
                null,
                MetadataCommandTimeoutSeconds);
        }

        public DataTable GetIndexes(string databaseName, string tableName)
        {
            string safeDB = databaseName.Replace("`", "``");
            string safeTable = tableName.Replace("`", "``");
            return ExecuteDataTable(
                $"SHOW INDEX FROM `{safeDB}`.`{safeTable}`;",
                null,
                MetadataCommandTimeoutSeconds);
        }

        public DataTable GetTableStatus(string databaseName)
        {
            string safeDB = databaseName.Replace("`", "``");
            return ExecuteDataTable(
                $"SHOW TABLE STATUS FROM `{safeDB}`;",
                null,
                MetadataCommandTimeoutSeconds);
        }

        public Dictionary<string, string> GetDatabaseInfo(string databaseName)
        {
            var output = new Dictionary<string, string>();
            var p = new Dictionary<string, object> { { "db", databaseName } };
            DataTable dt = ExecuteDataTable(
                "SELECT default_character_set_name, default_collation_name FROM information_schema.schemata WHERE schema_name = ?db",
                p,
                MetadataCommandTimeoutSeconds);
            if (dt.Rows.Count > 0)
            {
                output["character_set"] = dt.Rows[0]["default_character_set_name"].ToString();
                output["collation"] = dt.Rows[0]["default_collation_name"].ToString();
            }
            return output;
        }

        public string GetTableCreateStatement(string databaseName, string tableName)
        {
            string safeDB = databaseName.Replace("`", "``");
            string safeTable = tableName.Replace("`", "``");
            DataTable dt = SelectSQL($"SHOW CREATE TABLE `{safeDB}`.`{safeTable}`;");
            if (dt.Rows.Count > 0)
            {
                return dt.Rows[0][1].ToString();
            }
            return "";
        }

        public bool TableExists(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object> { { "db", databaseName }, { "name", tableName } };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = ?db AND table_name = ?name AND table_type = 'BASE TABLE';", p);
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) > 0;
        }

        public bool ViewExists(string databaseName, string viewName)
        {
            var p = new Dictionary<string, object> { { "db", databaseName }, { "name", viewName } };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM information_schema.views WHERE table_schema = ?db AND table_name = ?name;", p);
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) > 0;
        }

        public void RenameTable(string databaseName, string oldTableName, string newTableName)
        {
            string safeDB = databaseName.Replace("`", "``");
            string safeOld = oldTableName.Replace("`", "``");
            string safeNew = newTableName.Replace("`", "``");
            ExecOrThrow("RENAME TABLE `" + safeDB + "`.`" + safeOld + "` TO `" + safeDB + "`.`" + safeNew + "`;");
        }

        public void RenameView(string databaseName, string oldViewName, string newViewName)
        {
            string safeDB = databaseName.Replace("`", "``");
            string safeOld = oldViewName.Replace("`", "``");
            string safeNew = newViewName.Replace("`", "``");
            ExecOrThrow("RENAME TABLE `" + safeDB + "`.`" + safeOld + "` TO `" + safeDB + "`.`" + safeNew + "`;");
        }

        public long CountRows(string databaseName, string tableName)
        {
            string safeDB = databaseName.Replace("`", "``");
            string safeTable = tableName.Replace("`", "``");
            DataTable dt = SelectSQL($"SELECT COUNT(*) FROM `{safeDB}`.`{safeTable}`;");
            return dt.Rows.Count > 0 ? Convert.ToInt64(dt.Rows[0][0]) : 0;
        }

        public DataTable GetCopyColumns(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object> { { "db", databaseName }, { "name", tableName } };
            return SelectSQL(@"
                SELECT
                    COLUMN_NAME AS Name,
                    DATA_TYPE AS DataType,
                    IS_NULLABLE AS IsNullable,
                    CHARACTER_MAXIMUM_LENGTH AS MaxLength,
                    NUMERIC_PRECISION AS NumericPrecision,
                    NUMERIC_SCALE AS NumericScale,
                    COLUMN_DEFAULT AS DefaultValue,
                    COLUMN_COMMENT AS Comment,
                    ORDINAL_POSITION AS OrdinalPosition
                FROM information_schema.columns
                WHERE table_schema = ?db AND table_name = ?name
                ORDER BY ORDINAL_POSITION;", p);
        }

        public DataTable GetCopyIndexes(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object> { { "db", databaseName }, { "name", tableName } };
            return SelectSQL(@"
                SELECT
                    INDEX_NAME AS IndexName,
                    COLUMN_NAME AS ColumnName,
                    NON_UNIQUE AS NonUnique,
                    SEQ_IN_INDEX AS SeqInIndex,
                    INDEX_TYPE AS IndexType
                FROM information_schema.statistics
                WHERE table_schema = ?db AND table_name = ?name
                ORDER BY INDEX_NAME, SEQ_IN_INDEX;", p);
        }

        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider)
        {
            foreach (string sql in BuildMySqlCopyCreateTableStatements(databaseName, tableName, sourceColumns, sourceProvider))
            {
                ExecOrThrow(sql);
            }
        }

        private static List<string> BuildMySqlCopyCreateTableStatements(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider)
        {
            string safeDB = databaseName.Replace("`", "``");
            string safeTable = tableName.Replace("`", "``");
            List<string> defs = new List<string>();
            foreach (DataRow row in sourceColumns.Rows)
            {
                string name = row["Name"].ToString().Replace("`", "``");
                string nullable = IsCopyNullable(row) ? "NULL" : "NOT NULL";
                string defaultValue = GetMySqlCopyDefaultValue(row, sourceProvider);
                string comment = "";
                if (row.Table.Columns.Contains("Comment") && row["Comment"] != DBNull.Value && row["Comment"].ToString().Length > 0)
                    comment = " COMMENT '" + row["Comment"].ToString().Replace("'", "''") + "'";
                defs.Add("`" + name + "` " + MapCopyTypeToMySql(row) + (string.IsNullOrWhiteSpace(defaultValue) ? "" : " DEFAULT " + defaultValue) + " " + nullable + comment);
            }
            return new List<string> { "CREATE TABLE `" + safeDB + "`.`" + safeTable + "` (" + string.Join(", ", defs.ToArray()) + ");" };
        }

        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider)
        {
            if (sourceIndexes == null || sourceIndexes.Rows.Count == 0) return;
            string safeDB = databaseName.Replace("`", "``");
            string safeTable = tableName.Replace("`", "``");
            foreach (var group in sourceIndexes.AsEnumerable().GroupBy(r => r["IndexName"].ToString()))
            {
                string indexName = group.Key;
                if (string.IsNullOrEmpty(indexName) || indexName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) continue;
                bool unique = false;
                DataRow first = group.First();
                if (first.Table.Columns.Contains("NonUnique") && first["NonUnique"] != DBNull.Value)
                    unique = first["NonUnique"].ToString() == "0" || first["NonUnique"].ToString().Equals("False", StringComparison.OrdinalIgnoreCase);

                List<string> cols = new List<string>();
                foreach (DataRow row in group.OrderBy(r => Convert.ToInt32(r["SeqInIndex"])))
                    cols.Add("`" + row["ColumnName"].ToString().Replace("`", "``") + "`");

                string safeIndex = indexName.Replace("`", "``");
                string sql = "CREATE " + (unique ? "UNIQUE " : "") + "INDEX `" + safeIndex + "` ON `" + safeDB + "`.`" + safeTable + "` (" + string.Join(",", cols.ToArray()) + ");";
                ExecOrThrow(sql);
            }
        }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            string safeDB = databaseName.Replace("`", "``");
            string safeTable = tableName.Replace("`", "``");
            return SelectSQL($"SELECT * FROM `{safeDB}`.`{safeTable}` LIMIT {limit} OFFSET {offset};");
        }

        public void InsertTableBatch(string databaseName, string tableName, DataTable rows)
        {
            if (rows == null || rows.Rows.Count == 0) return;
            string safeDB = databaseName.Replace("`", "``");
            string safeTable = tableName.Replace("`", "``");
            List<string> cols = new List<string>();
            foreach (DataColumn col in rows.Columns) cols.Add("`" + col.ColumnName.Replace("`", "``") + "`");

            List<string> valueGroups = new List<string>();
            Dictionary<string, object> p = new Dictionary<string, object>();
            for (int r = 0; r < rows.Rows.Count; r++)
            {
                List<string> vals = new List<string>();
                for (int c = 0; c < rows.Columns.Count; c++)
                {
                    string key = "p" + r + "_" + c;
                    vals.Add("?" + key);
                    p[key] = rows.Rows[r][c] == DBNull.Value ? DBNull.Value : rows.Rows[r][c];
                }
                valueGroups.Add("(" + string.Join(",", vals.ToArray()) + ")");
            }

            string sql = "INSERT INTO `" + safeDB + "`.`" + safeTable + "` (" + string.Join(",", cols.ToArray()) + ") VALUES " + string.Join(",", valueGroups.ToArray()) + ";";
            ExecOrThrow(sql, p);
        }

        public string GetViewCreateStatement(string databaseName, string viewName)
        {
            string safeDB = databaseName.Replace("`", "``");
            string safeView = viewName.Replace("`", "``");
            DataTable dt = SelectSQL($"SHOW CREATE VIEW `{safeDB}`.`{safeView}`;");
            return dt.Rows.Count > 0 ? dt.Rows[0]["Create View"].ToString() : "";
        }

        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql)
        {
            string safeDB = databaseName.Replace("`", "``");
            string safeView = viewName.Replace("`", "``");
            string selectSql = ViewSqlDialectConverter.ExtractSelectSql(sourceViewSql);
            if (string.IsNullOrWhiteSpace(selectSql))
            {
                throw new Exception("無法解析 MySQL View DDL");
            }

            string sql = "CREATE VIEW `" + safeDB + "`.`" + safeView + "` AS " + selectSql.Trim().TrimEnd(';') + ";";
            ExecOrThrow(sql);
        }

        private void ExecOrThrow(string sql, Dictionary<string, object> parameters = null)
        {
            var res = ExecSQL(sql, parameters);
            if (!res.ContainsKey("status") || res["status"] != "OK")
                throw new Exception(res.ContainsKey("reason") ? res["reason"] : "SQL 執行失敗");
        }

        private static bool IsCopyNullable(DataRow row)
        {
            return !row.Table.Columns.Contains("IsNullable") || row["IsNullable"] == DBNull.Value || row["IsNullable"].ToString().ToUpper() != "NO";
        }

        private static string MapCopyTypeToMySql(DataRow row)
        {
            string type = row["DataType"].ToString().ToLower();
            if (type.Contains("bigint")) return "BIGINT";
            if (type.Contains("int") || type.Contains("serial")) return "INT";
            if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("money"))
            {
                int precision = GetOptionalInt(row, "NumericPrecision");
                int scale = GetOptionalInt(row, "NumericScale");
                if (precision > 0)
                {
                    if (scale < 0) scale = 0;
                    return "DECIMAL(" + precision + "," + scale + ")";
                }
                return "DECIMAL(38,10)";
            }
            if (type.Contains("double") || type.Contains("float") || type.Contains("real")) return "DOUBLE";
            if (type.Contains("bool") || type == "bit") return "TINYINT(1)";
            if (type.Contains("date") || type.Contains("time")) return "DATETIME";
            if (type.Contains("blob") || type.Contains("binary") || type.Contains("bytea") || type.Contains("image")) return "LONGBLOB";
            if (type.Contains("text") || type.Contains("json") || type.Contains("xml")) return "LONGTEXT";
            if (type.Contains("char") || type.Contains("string"))
            {
                int length = GetOptionalInt(row, "MaxLength");
                if (length > 0 && length <= 65535) return "VARCHAR(" + length + ")";
            }
            return "VARCHAR(255)";
        }

        private static string GetMySqlCopyDefaultValue(DataRow row, string sourceProvider)
        {
            if (!string.Equals(sourceProvider, "mysql", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            string defaultValue = GetOptionalString(row, "DefaultValue").Trim();
            if (string.IsNullOrWhiteSpace(defaultValue) || string.Equals(defaultValue, "NULL", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            if (IsMySqlDefaultExpression(defaultValue) || IsMySqlNumericType(row) || IsQuotedSqlLiteral(defaultValue))
            {
                return defaultValue;
            }

            return "'" + defaultValue.Replace("'", "''") + "'";
        }

        private static bool IsMySqlDefaultExpression(string defaultValue)
        {
            string normalized = defaultValue.Trim().ToUpperInvariant();
            return normalized.StartsWith("(") ||
                   normalized.StartsWith("CURRENT_TIMESTAMP") ||
                   normalized.StartsWith("CURRENT_DATE") ||
                   normalized.StartsWith("CURRENT_TIME") ||
                   normalized.StartsWith("LOCALTIME") ||
                   normalized.StartsWith("LOCALTIMESTAMP") ||
                   normalized.StartsWith("UUID()") ||
                   normalized.StartsWith("NOW()");
        }

        private static bool IsMySqlNumericType(DataRow row)
        {
            string type = GetOptionalString(row, "DataType").ToLowerInvariant();
            return type.Contains("int") ||
                   type.Contains("decimal") ||
                   type.Contains("numeric") ||
                   type.Contains("money") ||
                   type.Contains("double") ||
                   type.Contains("float") ||
                   type.Contains("real") ||
                   type.Contains("bool") ||
                   type == "bit";
        }

        private static bool IsQuotedSqlLiteral(string value)
        {
            string trimmed = value.Trim();
            return (trimmed.StartsWith("'") && trimmed.EndsWith("'")) ||
                   (trimmed.StartsWith("\"") && trimmed.EndsWith("\"")) ||
                   trimmed.StartsWith("b'", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("x'", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetOptionalString(DataRow row, string columnName)
        {
            if (row == null || row.Table == null || !row.Table.Columns.Contains(columnName) || row[columnName] == DBNull.Value) return "";
            return row[columnName].ToString();
        }

        private static int GetOptionalInt(DataRow row, string columnName)
        {
            string value = GetOptionalString(row, columnName).Trim();
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : -1;
        }

        public void Dispose()
        {
            Close();
            MCT?.Dispose();
        }

      
        public Dictionary<string, object> insertSQL_SAFE(string table, Dictionary<string, object> m)
        {
            Dictionary<string, object> output = new Dictionary<string, object>();
            try
            {
                int LAST_ID = -1;
                List<string> keys = new List<string>();
                List<string> qa = new List<string>();
                foreach (var key in m.Keys)
                {
                    keys.Add(key);
                    qa.Add("?" + key);
                }
                string SQL = @"
                INSERT INTO `" + table + @"`" +
                    @"(`"
                        + my.implode("`,`", keys) +
                    @"`)
                VALUES("
                        + my.implode(",", qa) +
                    @")";
                MC = new MySqlCommand(SQL, MCT);
                foreach (var key in m.Keys)
                {
                    PA = new MySqlParameter("?" + key, m[key]);
                    MC.Parameters.Add(PA);
                }
                LAST_ID = Convert.ToInt32(MC.ExecuteScalar());
                output["status"] = "OK";
                output["LAST_ID"] = LAST_ID;
                return output;
            }
            catch (Exception ex)
            {
                output["status"] = "NO";
                output["reason"] = ex.Message;
                return output;
            }
        }
        public Dictionary<string, object> updateSQL_SAFE(string table, Dictionary<string, object> m, string whereSQL, Dictionary<string, object> wm)
        {
            Dictionary<string, object> output = new Dictionary<string, object>();
            try
            {
                whereSQL = whereSQL.Replace("@", "?");
                List<string> fields = new List<string>();
                foreach (var key in m.Keys)
                {
                    fields.Add("`" + key + "`=?" + key);
                }
                string SQL = @"
                UPDATE `" + table + @"` SET " +
                     my.implode(",", fields) +
                @"
                    WHERE 
                        1=1
                        " + whereSQL + @"
                ";
                MC = new MySqlCommand(SQL, MCT);
                foreach (var key in m.Keys)
                {
                    PA = new MySqlParameter("?" + key, m[key]);
                    MC.Parameters.Add(PA);
                }
                foreach (var key in wm.Keys)
                {
                    PA = new MySqlParameter("?" + key, wm[key]);
                    MC.Parameters.Add(PA);
                }
                MC.ExecuteScalar();
                output["status"] = "OK";
                return output;
            }
            catch (Exception ex)
            {
                output["status"] = "NO";
                output["reason"] = ex.Message;
                return output;
            }
        }
    }
}
