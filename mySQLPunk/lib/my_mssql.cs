using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using utility;
using System.Data;
using System.Data.SqlClient;
namespace mySQLPunk.lib
{
    public class my_mssql : IDatabase
    {
        myinclude my = new myinclude();
        public SqlConnection MCT = null;
        public SqlCommand MC = null;
        public SqlParameter PA = null;

        public ConnectionState State => MCT?.State ?? ConnectionState.Closed;
        public string ProviderName => "mssql";

        public void SetConn(string connection)
        {
            MCT = new SqlConnection(connection);
        }
        public void setConn(string connection) => SetConn(connection);

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
            using (SqlCommand cmd = new SqlCommand(SQL, MCT))
            {
                foreach (var key in key_value.Keys)
                {
                    cmd.Parameters.Add(new SqlParameter("@" + key, key_value[key]));
                }
                using (SqlDataReader reader = cmd.ExecuteReader())
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
                using (SqlCommand cmd = new SqlCommand(SQL, MCT))
                {
                    foreach (var key in m.Keys)
                    {
                        cmd.Parameters.Add(new SqlParameter("@" + key, m[key]));
                    }
                    cmd.ExecuteNonQuery();
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
            using (SqlCommand cmd = new SqlCommand(sql, MCT))
            {
                if (parameters != null)
                {
                    foreach (var key in parameters.Keys)
                    {
                        cmd.Parameters.Add(new SqlParameter("@" + key, parameters[key]));
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
                using (SqlCommand cmd = new SqlCommand(sql, MCT))
                {
                    if (parameters != null)
                    {
                        foreach (var key in parameters.Keys)
                        {
                            cmd.Parameters.Add(new SqlParameter("@" + key, parameters[key]));
                        }
                    }
                    await cmd.ExecuteNonQueryAsync();
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
            DataTable dt = SelectSQL("SELECT name AS [Database] FROM sys.databases WHERE name NOT IN ('master', 'tempdb', 'model', 'msdb');");
            foreach (DataRow row in dt.Rows)
            {
                dbs.Add(row[0].ToString());
            }
            return dbs;
        }

        public List<string> GetTables(string databaseName)
        {
            List<string> tables = new List<string>();
            DataTable dt = SelectSQL("SELECT TABLE_NAME FROM [" + EscapeSqlServerName(databaseName) + "].INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_TYPE = 'BASE TABLE';");
            foreach (DataRow row in dt.Rows)
            {
                tables.Add(row[0].ToString());
            }
            return tables;
        }

        public List<string> GetViews(string databaseName)
        {
            List<string> views = new List<string>();
            DataTable dt = SelectSQL("SELECT TABLE_NAME FROM [" + EscapeSqlServerName(databaseName) + "].INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_TYPE = 'VIEW';");
            foreach (DataRow row in dt.Rows)
            {
                views.Add(row[0].ToString());
            }
            return views;
        }

        public DataTable GetColumns(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object> { { "tableName", tableName } };
            return SelectSQL(@"
                SELECT
                    COLUMN_NAME,
                    DATA_TYPE,
                    IS_NULLABLE,
                    COLUMN_DEFAULT,
                    CHARACTER_MAXIMUM_LENGTH,
                    NUMERIC_PRECISION,
                    NUMERIC_SCALE
                FROM [" + EscapeSqlServerName(databaseName) + @"].INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @tableName
                ORDER BY ORDINAL_POSITION", p);
        }

        public DataTable GetTableStatus(string databaseName)
        {
            return SelectSQL(@"
                SELECT
                    t.name AS [Name],
                    NULL AS [Auto_increment],
                    t.modify_date AS [Update_time],
                    t.create_date AS [Create_time],
                    NULL AS [Check_time],
                    COALESCE(ds.Data_length, 0) AS [Data_length],
                    COALESCE(ds.Index_length, 0) AS [Index_length],
                    CAST(0 AS BIGINT) AS [Max_data_length],
                    CAST(0 AS BIGINT) AS [Data_free],
                    'SQL Server' AS [Engine],
                    COALESCE(ds.[Rows], 0) AS [Rows],
                    COALESCE(CAST(ep.value AS NVARCHAR(4000)), '') AS [Comment],
                    '' AS [Row_format],
                    DATABASEPROPERTYEX('" + EscapeSqlLiteral(databaseName) + @"', 'Collation') AS [Collation],
                    '' AS [Create_options]
                FROM [" + EscapeSqlServerName(databaseName) + @"].sys.tables t
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.schemas s ON s.schema_id = t.schema_id
                LEFT JOIN (
                    SELECT
                        object_id,
                        CAST(SUM(CASE WHEN index_id IN (0, 1) THEN row_count ELSE 0 END) AS BIGINT) AS [Rows],
                        CAST(SUM(CASE WHEN index_id IN (0, 1) THEN used_page_count ELSE 0 END) * 8 * 1024 AS BIGINT) AS [Data_length],
                        CAST(SUM(CASE WHEN index_id > 1 THEN used_page_count ELSE 0 END) * 8 * 1024 AS BIGINT) AS [Index_length]
                    FROM [" + EscapeSqlServerName(databaseName) + @"].sys.dm_db_partition_stats
                    GROUP BY object_id
                ) ds ON ds.object_id = t.object_id
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.extended_properties ep ON ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
                WHERE s.name = 'dbo'
                ORDER BY t.name;");
        }

        public DataTable GetIndexes(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object> { { "name", tableName } };
            return SelectSQL(@"
                SELECT
                    CASE WHEN i.is_primary_key = 1 THEN 'PRIMARY' ELSE i.name END AS [Key_name],
                    c.name AS [Column_name],
                    CASE WHEN i.is_unique = 1 OR i.is_primary_key = 1 THEN 0 ELSE 1 END AS [Non_unique],
                    ic.key_ordinal AS [Seq_in_index],
                    i.type_desc AS [Index_type],
                    '' AS [Index_comment]
                FROM [" + EscapeSqlServerName(databaseName) + @"].sys.indexes i
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.objects o ON o.object_id = i.object_id
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.schemas s ON s.schema_id = o.schema_id
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id
                WHERE s.name = 'dbo' AND o.name = @name AND i.name IS NOT NULL
                ORDER BY i.name, ic.key_ordinal;", p);
        }

        public Dictionary<string, string> GetDatabaseInfo(string databaseName)
        {
            var output = new Dictionary<string, string>();
            var p = new Dictionary<string, object> { { "name", databaseName } };
            DataTable dt = SelectSQL("SELECT collation_name FROM sys.databases WHERE name = @name;", p);
            output["character_set"] = "UTF-16";
            output["collation"] = dt.Rows.Count > 0 ? dt.Rows[0]["collation_name"].ToString() : "";
            return output;
        }

        public string GetTableCreateStatement(string databaseName, string tableName)
        {
            DataTable columns = GetCopyColumns(databaseName, tableName);
            if (columns.Rows.Count == 0) return "";

            List<string> definitions = new List<string>();
            foreach (DataRow row in columns.Rows)
            {
                string nullable = IsCopyNullable(row) ? "NULL" : "NOT NULL";
                definitions.Add("  [" + EscapeSqlServerName(row["Name"].ToString()) + "] " + MapCopyTypeToSqlServer(row) + " " + nullable);
            }

            return "CREATE TABLE [" + EscapeSqlServerName(databaseName) + "].[dbo].[" + EscapeSqlServerName(tableName) + "] (\r\n" +
                   string.Join(",\r\n", definitions.ToArray()) +
                   "\r\n);";
        }

        public bool TableExists(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object> { { "name", tableName } };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM [" + EscapeSqlServerName(databaseName) + "].INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @name AND TABLE_TYPE = 'BASE TABLE';", p);
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) > 0;
        }

        public bool ViewExists(string databaseName, string viewName)
        {
            var p = new Dictionary<string, object> { { "name", viewName } };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM [" + EscapeSqlServerName(databaseName) + "].INFORMATION_SCHEMA.VIEWS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @name;", p);
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) > 0;
        }

        public void RenameTable(string databaseName, string oldTableName, string newTableName)
        {
            RenameSqlServerObject(databaseName, oldTableName, newTableName);
        }

        public void RenameView(string databaseName, string oldViewName, string newViewName)
        {
            RenameSqlServerObject(databaseName, oldViewName, newViewName);
        }

        public long CountRows(string databaseName, string tableName)
        {
            DataTable dt = SelectSQL("SELECT COUNT_BIG(*) FROM [" + EscapeSqlServerName(databaseName) + "].[dbo].[" + EscapeSqlServerName(tableName) + "];");
            return dt.Rows.Count > 0 ? Convert.ToInt64(dt.Rows[0][0]) : 0;
        }

        public DataTable GetCopyColumns(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object> { { "name", tableName } };
            return SelectSQL(@"
                SELECT
                    c.COLUMN_NAME AS Name,
                    c.DATA_TYPE AS DataType,
                    c.IS_NULLABLE AS IsNullable,
                    c.CHARACTER_MAXIMUM_LENGTH AS MaxLength,
                    c.NUMERIC_PRECISION AS NumericPrecision,
                    c.NUMERIC_SCALE AS NumericScale,
                    CAST(ep.value AS NVARCHAR(4000)) AS Comment,
                    c.ORDINAL_POSITION AS OrdinalPosition
                FROM [" + EscapeSqlServerName(databaseName) + @"].INFORMATION_SCHEMA.COLUMNS c
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.objects o ON o.name = c.TABLE_NAME
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.columns sc ON sc.object_id = o.object_id AND sc.name = c.COLUMN_NAME
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.extended_properties ep ON ep.major_id = sc.object_id AND ep.minor_id = sc.column_id AND ep.name = 'MS_Description'
                WHERE c.TABLE_SCHEMA = 'dbo' AND c.TABLE_NAME = @name
                ORDER BY c.ORDINAL_POSITION;", p);
        }

        public DataTable GetCopyIndexes(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object> { { "name", tableName } };
            return SelectSQL(@"
                SELECT
                    i.name AS IndexName,
                    c.name AS ColumnName,
                    CASE WHEN i.is_unique = 1 THEN 0 ELSE 1 END AS NonUnique,
                    ic.key_ordinal AS SeqInIndex,
                    i.type_desc AS IndexType
                FROM [" + EscapeSqlServerName(databaseName) + @"].sys.indexes i
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.objects o ON o.object_id = i.object_id
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id
                WHERE o.name = @name AND i.name IS NOT NULL AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
                ORDER BY i.name, ic.key_ordinal;", p);
        }

        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider)
        {
            List<string> defs = new List<string>();
            foreach (DataRow row in sourceColumns.Rows)
            {
                string nullable = IsCopyNullable(row) ? "NULL" : "NOT NULL";
                defs.Add("[" + EscapeSqlServerName(row["Name"].ToString()) + "] " + MapCopyTypeToSqlServer(row) + " " + nullable);
            }
            ExecOrThrow("CREATE TABLE [" + EscapeSqlServerName(databaseName) + "].[dbo].[" + EscapeSqlServerName(tableName) + "] (" + string.Join(", ", defs.ToArray()) + ");");

            foreach (DataRow row in sourceColumns.Rows)
            {
                if (!row.Table.Columns.Contains("Comment") || row["Comment"] == DBNull.Value || row["Comment"].ToString().Length == 0) continue;
                var p = new Dictionary<string, object> { { "value", row["Comment"].ToString() } };
                string sql = "EXEC [" + EscapeSqlServerName(databaseName) + "].sys.sp_addextendedproperty @name=N'MS_Description', @value=@value, @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'" + EscapeSqlLiteral(tableName) + "', @level2type=N'COLUMN', @level2name=N'" + EscapeSqlLiteral(row["Name"].ToString()) + "';";
                ExecOrThrow(sql, p);
            }
        }

        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider)
        {
            if (sourceIndexes == null || sourceIndexes.Rows.Count == 0) return;
            foreach (var group in sourceIndexes.AsEnumerable().GroupBy(r => r["IndexName"].ToString()))
            {
                string indexName = group.Key;
                if (string.IsNullOrEmpty(indexName) || indexName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) continue;
                DataRow first = group.First();
                bool unique = first.Table.Columns.Contains("NonUnique") && first["NonUnique"] != DBNull.Value &&
                    (first["NonUnique"].ToString() == "0" || first["NonUnique"].ToString().Equals("False", StringComparison.OrdinalIgnoreCase));
                List<string> cols = new List<string>();
                foreach (DataRow row in group.OrderBy(r => Convert.ToInt32(r["SeqInIndex"])))
                    cols.Add("[" + EscapeSqlServerName(row["ColumnName"].ToString()) + "]");
                string targetIndexName = tableName + "_" + indexName;
                string sql = "CREATE " + (unique ? "UNIQUE " : "") + "INDEX [" + EscapeSqlServerName(targetIndexName) + "] ON [" + EscapeSqlServerName(databaseName) + "].[dbo].[" + EscapeSqlServerName(tableName) + "] (" + string.Join(",", cols.ToArray()) + ");";
                ExecOrThrow(sql);
            }
        }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            return SelectSQL("SELECT * FROM [" + EscapeSqlServerName(databaseName) + "].[dbo].[" + EscapeSqlServerName(tableName) + "] ORDER BY (SELECT NULL) OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY;");
        }

        public void InsertTableBatch(string databaseName, string tableName, DataTable rows)
        {
            if (rows == null || rows.Rows.Count == 0) return;
            List<string> cols = new List<string>();
            foreach (DataColumn col in rows.Columns) cols.Add("[" + EscapeSqlServerName(col.ColumnName) + "]");
            List<string> valueGroups = new List<string>();
            Dictionary<string, object> p = new Dictionary<string, object>();
            for (int r = 0; r < rows.Rows.Count; r++)
            {
                List<string> vals = new List<string>();
                for (int c = 0; c < rows.Columns.Count; c++)
                {
                    string key = "p" + r + "_" + c;
                    vals.Add("@" + key);
                    p[key] = rows.Rows[r][c] == DBNull.Value ? DBNull.Value : rows.Rows[r][c];
                }
                valueGroups.Add("(" + string.Join(",", vals.ToArray()) + ")");
            }
            string sql = "INSERT INTO [" + EscapeSqlServerName(databaseName) + "].[dbo].[" + EscapeSqlServerName(tableName) + "] (" + string.Join(",", cols.ToArray()) + ") VALUES " + string.Join(",", valueGroups.ToArray()) + ";";
            ExecOrThrow(sql, p);
        }

        public string GetViewCreateStatement(string databaseName, string viewName)
        {
            var p = new Dictionary<string, object> { { "fullName", databaseName + ".dbo." + viewName } };
            DataTable dt = SelectSQL("SELECT OBJECT_DEFINITION(OBJECT_ID(@fullName));", p);
            return dt.Rows.Count > 0 ? dt.Rows[0][0].ToString() : "";
        }

        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql)
        {
            string selectSql = ViewSqlDialectConverter.ExtractSelectSql(sourceViewSql);
            if (string.IsNullOrWhiteSpace(selectSql))
            {
                throw new Exception("無法解析 SQL Server View DDL");
            }

            string sql = "CREATE VIEW [dbo].[" + EscapeSqlServerName(viewName) + "] AS " + selectSql.Trim().TrimEnd(';') + ";";
            string originalDb = MCT.Database;
            try
            {
                MCT.ChangeDatabase(databaseName);
                ExecOrThrow(sql);
            }
            finally
            {
                MCT.ChangeDatabase(originalDb);
            }
        }

        private void ExecOrThrow(string sql, Dictionary<string, object> parameters = null)
        {
            var res = ExecSQL(sql, parameters);
            if (!res.ContainsKey("status") || res["status"] != "OK")
                throw new Exception(res.ContainsKey("reason") ? res["reason"] : "SQL 執行失敗");
        }

        private void RenameSqlServerObject(string databaseName, string oldName, string newName)
        {
            string originalDb = MCT.Database;
            try
            {
                MCT.ChangeDatabase(databaseName);
                string sql = "EXEC sp_rename N'dbo." + EscapeSqlLiteral(oldName) + "', N'" + EscapeSqlLiteral(newName) + "', N'OBJECT';";
                ExecOrThrow(sql);
            }
            finally
            {
                MCT.ChangeDatabase(originalDb);
            }
        }

        private static string EscapeSqlServerName(string name) => name.Replace("]", "]]");
        private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

        private static bool IsCopyNullable(DataRow row)
        {
            return !row.Table.Columns.Contains("IsNullable") || row["IsNullable"] == DBNull.Value || row["IsNullable"].ToString().ToUpper() != "NO";
        }

        private static string MapCopyTypeToSqlServer(DataRow row)
        {
            string type = row["DataType"].ToString().ToLower();
            if (type.Contains("bigint")) return "BIGINT";
            if (type.Contains("smallint")) return "SMALLINT";
            if (type.Contains("tinyint")) return "TINYINT";
            if (type.Contains("int") || type.Contains("serial")) return "INT";
            if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("money")) return "DECIMAL(38,10)";
            if (type.Contains("double") || type.Contains("float")) return "FLOAT";
            if (type.Contains("real")) return "REAL";
            if (type.Contains("bool") || type == "bit") return "BIT";
            if (type.Contains("date") || type.Contains("time")) return "DATETIME2";
            if (type.Contains("blob") || type.Contains("binary") || type.Contains("bytea") || type.Contains("image")) return "VARBINARY(MAX)";
            return "NVARCHAR(MAX)";
        }

        public void Dispose()
        {
            Close();
            MCT?.Dispose();
        }

        // ... 保留 insertSQL_SAFE 和 updateSQL_SAFE ...
       
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
                    qa.Add("@" + key);
                }
                string SQL = @"
                INSERT INTO [" + table + @"]" +
                    @"(["
                        + my.implode("],[", keys) +
                    @"])
                VALUES("
                        + my.implode(",", qa) +
                    @")";
                MC = new SqlCommand(SQL, MCT);
                foreach (var key in m.Keys)
                {
                    PA = new SqlParameter("@" + key, m[key]);
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
                whereSQL = whereSQL.Replace("@", "@");
                List<string> fields = new List<string>();
                foreach (var key in m.Keys)
                {
                    fields.Add("[" + key + "]=@" + key);
                }
                string SQL = @"
                UPDATE [" + table + @"] SET " +
                     my.implode(",", fields) +
                @"
                    WHERE 
                        1=1
                        " + whereSQL + @"
                ";
                MC = new SqlCommand(SQL, MCT);
                foreach (var key in m.Keys)
                {
                    PA = new SqlParameter("@" + key, m[key]);
                    MC.Parameters.Add(PA);
                }
                foreach (var key in wm.Keys)
                {
                    PA = new SqlParameter("@" + key, wm[key]);
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
