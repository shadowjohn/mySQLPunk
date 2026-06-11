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
                    output["rowsAffected"] = cmd.ExecuteNonQuery().ToString();
                }
                output["status"] = "OK";
                return output;
            }
            catch (Exception ex)
            {
                output["status"] = "NO";
                output["reason"] = ExceptionMessageService.GetReason(ex);
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
                    output["rowsAffected"] = (await cmd.ExecuteNonQueryAsync()).ToString();
                }
                output["status"] = "OK";
                return output;
            }
            catch (Exception ex)
            {
                output["status"] = "NO";
                output["reason"] = ExceptionMessageService.GetReason(ex);
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
            DataTable dt = SelectSQL("SELECT CASE WHEN TABLE_SCHEMA = 'dbo' THEN TABLE_NAME ELSE TABLE_SCHEMA + '.' + TABLE_NAME END AS TABLE_NAME FROM [" + EscapeSqlServerName(databaseName) + "].INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_SCHEMA, TABLE_NAME;");
            foreach (DataRow row in dt.Rows)
            {
                tables.Add(row[0].ToString());
            }
            return tables;
        }

        public List<string> GetViews(string databaseName)
        {
            List<string> views = new List<string>();
            DataTable dt = SelectSQL("SELECT CASE WHEN TABLE_SCHEMA = 'dbo' THEN TABLE_NAME ELSE TABLE_SCHEMA + '.' + TABLE_NAME END AS TABLE_NAME FROM [" + EscapeSqlServerName(databaseName) + "].INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'VIEW' ORDER BY TABLE_SCHEMA, TABLE_NAME;");
            foreach (DataRow row in dt.Rows)
            {
                views.Add(row[0].ToString());
            }
            return views;
        }

        public DataTable GetColumns(string databaseName, string tableName)
        {
            SqlServerObjectName target = ParseSqlServerObjectName(tableName);
            var p = new Dictionary<string, object> { { "schemaName", target.Schema }, { "tableName", target.Name } };
            return SelectSQL(@"
                SELECT
                    c.COLUMN_NAME,
                    c.DATA_TYPE,
                    c.IS_NULLABLE,
                    c.COLUMN_DEFAULT,
                    c.CHARACTER_MAXIMUM_LENGTH,
                    c.NUMERIC_PRECISION,
                    c.NUMERIC_SCALE,
                    COALESCE(CAST(ep.value AS NVARCHAR(4000)), '') AS [Comment]
                FROM [" + EscapeSqlServerName(databaseName) + @"].INFORMATION_SCHEMA.COLUMNS c
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.schemas s ON s.name = c.TABLE_SCHEMA
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.objects o ON o.name = c.TABLE_NAME AND o.schema_id = s.schema_id
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.columns sc ON sc.object_id = o.object_id AND sc.name = c.COLUMN_NAME
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.extended_properties ep ON ep.major_id = sc.object_id AND ep.minor_id = sc.column_id AND ep.name = 'MS_Description'
                WHERE c.TABLE_SCHEMA = @schemaName AND c.TABLE_NAME = @tableName
                ORDER BY c.ORDINAL_POSITION", p);
        }

        public DataTable GetTableStatus(string databaseName)
        {
            return SelectSQL(@"
                SELECT
                    CASE WHEN s.name = 'dbo' THEN t.name ELSE s.name + '.' + t.name END AS [Name],
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
                ORDER BY s.name, t.name;");
        }

        public DataTable GetIndexes(string databaseName, string tableName)
        {
            SqlServerObjectName target = ParseSqlServerObjectName(tableName);
            var p = new Dictionary<string, object> { { "schemaName", target.Schema }, { "name", target.Name } };
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
                WHERE s.name = @schemaName AND o.name = @name AND i.name IS NOT NULL
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

            DataTable indexes = GetIndexes(databaseName, tableName);
            DataTable copyIndexes = GetCopyIndexes(databaseName, tableName);
            return BuildSqlServerTableCreateStatement(databaseName, tableName, columns, indexes, copyIndexes);
        }

        private static string BuildSqlServerTableCreateStatement(string databaseName, string tableName, DataTable columns, DataTable indexes, DataTable copyIndexes)
        {
            SqlServerObjectName target = ParseSqlServerObjectName(tableName);
            List<string> definitions = new List<string>();
            foreach (DataRow row in columns.Rows)
            {
                string nullable = IsCopyNullable(row) ? "NULL" : "NOT NULL";
                string defaultValue = GetOptionalString(row, "DefaultValue").Trim();
                string defaultSql = string.IsNullOrWhiteSpace(defaultValue) ? "" : " DEFAULT " + defaultValue;
                definitions.Add("  [" + EscapeSqlServerName(row["Name"].ToString()) + "] " + MapCopyTypeToSqlServer(row) + defaultSql + " " + nullable);
            }

            List<string> primaryColumns = GetSqlServerPrimaryKeyColumns(indexes);
            if (primaryColumns.Count > 0)
            {
                definitions.Add("  CONSTRAINT [PK_" + EscapeSqlServerName(target.Name) + "] PRIMARY KEY (" + string.Join(", ", primaryColumns.ToArray()) + ")");
            }

            List<string> statements = new List<string>();
            statements.Add("CREATE TABLE " + BuildSqlServerQualifiedName(databaseName, target) + " (\r\n" +
                           string.Join(",\r\n", definitions.ToArray()) +
                           "\r\n);");

            foreach (DataRow row in columns.Rows)
            {
                string comment = GetOptionalString(row, "Comment");
                if (string.IsNullOrWhiteSpace(comment)) continue;

                statements.Add("EXEC [" + EscapeSqlServerName(databaseName) + "].sys.sp_addextendedproperty " +
                               "@name=N'MS_Description', @value=N'" + EscapeSqlLiteral(comment) + "', " +
                               "@level0type=N'SCHEMA', @level0name=N'" + EscapeSqlLiteral(target.Schema) + "', " +
                               "@level1type=N'TABLE', @level1name=N'" + EscapeSqlLiteral(target.Name) + "', " +
                               "@level2type=N'COLUMN', @level2name=N'" + EscapeSqlLiteral(row["Name"].ToString()) + "';");
            }

            statements.AddRange(BuildSqlServerIndexCreateStatements(databaseName, tableName, copyIndexes));
            return string.Join("\r\n", statements.ToArray());
        }

        private static List<string> GetSqlServerPrimaryKeyColumns(DataTable indexes)
        {
            List<DataRow> primaryRows = new List<DataRow>();
            if (indexes == null) return new List<string>();

            foreach (DataRow row in indexes.Rows)
            {
                if (GetOptionalString(row, "Key_name").Equals("PRIMARY", StringComparison.OrdinalIgnoreCase))
                {
                    primaryRows.Add(row);
                }
            }

            primaryRows.Sort((a, b) => GetOptionalInt(a, "Seq_in_index", 0).CompareTo(GetOptionalInt(b, "Seq_in_index", 0)));

            List<string> columns = new List<string>();
            foreach (DataRow row in primaryRows)
            {
                string columnName = GetOptionalString(row, "Column_name");
                if (!string.IsNullOrWhiteSpace(columnName))
                {
                    columns.Add("[" + EscapeSqlServerName(columnName) + "]");
                }
            }
            return columns;
        }

        private static List<string> BuildSqlServerIndexCreateStatements(string databaseName, string tableName, DataTable indexes)
        {
            List<string> statements = new List<string>();
            if (indexes == null || indexes.Rows.Count == 0) return statements;
            SqlServerObjectName target = ParseSqlServerObjectName(tableName);

            foreach (var group in indexes.AsEnumerable().GroupBy(r => r["IndexName"].ToString()))
            {
                string indexName = group.Key;
                if (string.IsNullOrWhiteSpace(indexName) || indexName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) continue;

                DataRow first = group.First();
                bool unique = first.Table.Columns.Contains("NonUnique") && first["NonUnique"] != DBNull.Value &&
                    (first["NonUnique"].ToString() == "0" || first["NonUnique"].ToString().Equals("False", StringComparison.OrdinalIgnoreCase));
                string indexType = GetSqlServerIndexType(GetOptionalString(first, "IndexType"));

                List<DataRow> orderedRows = group.ToList();
                orderedRows.Sort((a, b) => GetOptionalInt(a, "SeqInIndex", 0).CompareTo(GetOptionalInt(b, "SeqInIndex", 0)));

                List<string> cols = new List<string>();
                foreach (DataRow row in orderedRows)
                {
                    string columnName = GetOptionalString(row, "ColumnName");
                    if (!string.IsNullOrWhiteSpace(columnName))
                    {
                        cols.Add("[" + EscapeSqlServerName(columnName) + "]");
                    }
                }
                if (cols.Count == 0) continue;

                statements.Add("CREATE " + (unique ? "UNIQUE " : "") + indexType + "INDEX [" + EscapeSqlServerName(indexName) + "] ON " +
                               BuildSqlServerQualifiedName(databaseName, target) + " (" +
                               string.Join(", ", cols.ToArray()) + ");");
            }

            return statements;
        }

        private static string GetSqlServerIndexType(string indexType)
        {
            if (indexType != null && indexType.IndexOf("NONCLUSTERED", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "NONCLUSTERED ";
            }
            if (indexType != null && indexType.IndexOf("CLUSTERED", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "CLUSTERED ";
            }
            return "NONCLUSTERED ";
        }

        public bool TableExists(string databaseName, string tableName)
        {
            SqlServerObjectName target = ParseSqlServerObjectName(tableName);
            var p = new Dictionary<string, object> { { "schemaName", target.Schema }, { "name", target.Name } };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM [" + EscapeSqlServerName(databaseName) + "].INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @schemaName AND TABLE_NAME = @name AND TABLE_TYPE = 'BASE TABLE';", p);
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) > 0;
        }

        public bool ViewExists(string databaseName, string viewName)
        {
            SqlServerObjectName target = ParseSqlServerObjectName(viewName);
            var p = new Dictionary<string, object> { { "schemaName", target.Schema }, { "name", target.Name } };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM [" + EscapeSqlServerName(databaseName) + "].INFORMATION_SCHEMA.VIEWS WHERE TABLE_SCHEMA = @schemaName AND TABLE_NAME = @name;", p);
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
            DataTable dt = SelectSQL("SELECT COUNT_BIG(*) FROM " + BuildSqlServerQualifiedName(databaseName, tableName) + ";");
            return dt.Rows.Count > 0 ? Convert.ToInt64(dt.Rows[0][0]) : 0;
        }

        public DataTable GetCopyColumns(string databaseName, string tableName)
        {
            SqlServerObjectName target = ParseSqlServerObjectName(tableName);
            var p = new Dictionary<string, object> { { "schemaName", target.Schema }, { "name", target.Name } };
            return SelectSQL(@"
                SELECT
                    c.COLUMN_NAME AS Name,
                    c.DATA_TYPE AS DataType,
                    c.IS_NULLABLE AS IsNullable,
                    c.CHARACTER_MAXIMUM_LENGTH AS MaxLength,
                    c.NUMERIC_PRECISION AS NumericPrecision,
                    c.NUMERIC_SCALE AS NumericScale,
                    c.COLUMN_DEFAULT AS DefaultValue,
                    CAST(ep.value AS NVARCHAR(4000)) AS Comment,
                    c.ORDINAL_POSITION AS OrdinalPosition
                FROM [" + EscapeSqlServerName(databaseName) + @"].INFORMATION_SCHEMA.COLUMNS c
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.schemas s ON s.name = c.TABLE_SCHEMA
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.objects o ON o.name = c.TABLE_NAME AND o.schema_id = s.schema_id
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.columns sc ON sc.object_id = o.object_id AND sc.name = c.COLUMN_NAME
                LEFT JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.extended_properties ep ON ep.major_id = sc.object_id AND ep.minor_id = sc.column_id AND ep.name = 'MS_Description'
                WHERE c.TABLE_SCHEMA = @schemaName AND c.TABLE_NAME = @name
                ORDER BY c.ORDINAL_POSITION;", p);
        }

        public DataTable GetCopyIndexes(string databaseName, string tableName)
        {
            SqlServerObjectName target = ParseSqlServerObjectName(tableName);
            var p = new Dictionary<string, object> { { "schemaName", target.Schema }, { "name", target.Name } };
            return SelectSQL(@"
                SELECT
                    i.name AS IndexName,
                    c.name AS ColumnName,
                    CASE WHEN i.is_unique = 1 THEN 0 ELSE 1 END AS NonUnique,
                    ic.key_ordinal AS SeqInIndex,
                    i.type_desc AS IndexType
                FROM [" + EscapeSqlServerName(databaseName) + @"].sys.indexes i
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.objects o ON o.object_id = i.object_id
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.schemas s ON s.schema_id = o.schema_id
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                INNER JOIN [" + EscapeSqlServerName(databaseName) + @"].sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id
                WHERE s.name = @schemaName AND o.name = @name AND i.name IS NOT NULL AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
                ORDER BY i.name, ic.key_ordinal;", p);
        }

        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider)
        {
            foreach (string sql in BuildSqlServerCopyCreateTableStatements(databaseName, tableName, sourceColumns, sourceProvider))
            {
                ExecOrThrow(sql);
            }
        }

        private static List<string> BuildSqlServerCopyCreateTableStatements(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider)
        {
            SqlServerObjectName target = ParseSqlServerObjectName(tableName);
            List<string> statements = new List<string>();
            List<string> defs = new List<string>();
            foreach (DataRow row in sourceColumns.Rows)
            {
                string nullable = IsCopyNullable(row) ? "NULL" : "NOT NULL";
                string definition = "[" + EscapeSqlServerName(row["Name"].ToString()) + "] " + MapCopyTypeToSqlServer(row);
                string defaultValue = GetSqlServerCopyDefaultValue(row, sourceProvider);
                if (!string.IsNullOrWhiteSpace(defaultValue))
                {
                    definition += " DEFAULT " + defaultValue;
                }
                definition += " " + nullable;
                defs.Add(definition);
            }
            statements.Add("CREATE TABLE " + BuildSqlServerQualifiedName(databaseName, target) + " (" + string.Join(", ", defs.ToArray()) + ");");

            foreach (DataRow row in sourceColumns.Rows)
            {
                string comment = GetOptionalString(row, "Comment");
                if (string.IsNullOrWhiteSpace(comment)) continue;
                statements.Add("EXEC [" + EscapeSqlServerName(databaseName) + "].sys.sp_addextendedproperty " +
                               "@name=N'MS_Description', @value=N'" + EscapeSqlLiteral(comment) + "', " +
                               "@level0type=N'SCHEMA', @level0name=N'" + EscapeSqlLiteral(target.Schema) + "', " +
                               "@level1type=N'TABLE', @level1name=N'" + EscapeSqlLiteral(target.Name) + "', " +
                               "@level2type=N'COLUMN', @level2name=N'" + EscapeSqlLiteral(row["Name"].ToString()) + "';");
            }

            return statements;
        }

        private static string GetSqlServerCopyDefaultValue(DataRow row, string sourceProvider)
        {
            if (!string.Equals(sourceProvider, "mssql", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sourceProvider, "sqlserver", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            return GetOptionalString(row, "DefaultValue").Trim();
        }

        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider)
        {
            if (sourceIndexes == null || sourceIndexes.Rows.Count == 0) return;
            SqlServerObjectName target = ParseSqlServerObjectName(tableName);
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
                string targetIndexName = target.Name + "_" + indexName;
                string sql = "CREATE " + (unique ? "UNIQUE " : "") + "INDEX [" + EscapeSqlServerName(targetIndexName) + "] ON " + BuildSqlServerQualifiedName(databaseName, target) + " (" + string.Join(",", cols.ToArray()) + ");";
                ExecOrThrow(sql);
            }
        }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            return SelectSQL("SELECT * FROM " + BuildSqlServerQualifiedName(databaseName, tableName) + " ORDER BY (SELECT NULL) OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY;");
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
            string sql = "INSERT INTO " + BuildSqlServerQualifiedName(databaseName, tableName) + " (" + string.Join(",", cols.ToArray()) + ") VALUES " + string.Join(",", valueGroups.ToArray()) + ";";
            ExecOrThrow(sql, p);
        }

        public string GetViewCreateStatement(string databaseName, string viewName)
        {
            SqlServerObjectName target = ParseSqlServerObjectName(viewName);
            var p = new Dictionary<string, object> { { "fullName", databaseName + "." + target.Schema + "." + target.Name } };
            DataTable dt = SelectSQL("SELECT OBJECT_DEFINITION(OBJECT_ID(@fullName));", p);
            return dt.Rows.Count > 0 ? dt.Rows[0][0].ToString() : "";
        }

        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql)
        {
            string selectSql = ViewSqlDialectConverter.ExtractSelectSql(sourceViewSql);
            if (string.IsNullOrWhiteSpace(selectSql))
            {
                throw new Exception(Localization.Format("Object.ViewDdlParseFailed", "SQL Server"));
            }

            SqlServerObjectName target = ParseSqlServerObjectName(viewName);
            string sql = "CREATE VIEW " + BuildSqlServerTwoPartName(target) + " AS " + selectSql.Trim().TrimEnd(';') + ";";
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
                throw new Exception(DatabaseExecutionResultService.GetFailureReason(res));
        }

        private void RenameSqlServerObject(string databaseName, string oldName, string newName)
        {
            SqlServerObjectName oldTarget = ParseSqlServerObjectName(oldName);
            SqlServerObjectName newTarget = ParseSqlServerObjectName(newName);
            string originalDb = MCT.Database;
            try
            {
                MCT.ChangeDatabase(databaseName);
                string sql = "EXEC sp_rename N'" + EscapeSqlLiteral(oldTarget.Schema) + "." + EscapeSqlLiteral(oldTarget.Name) + "', N'" + EscapeSqlLiteral(newTarget.Name) + "', N'OBJECT';";
                ExecOrThrow(sql);
            }
            finally
            {
                MCT.ChangeDatabase(originalDb);
            }
        }

        private static string EscapeSqlServerName(string name) => name.Replace("]", "]]");
        private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

        private struct SqlServerObjectName
        {
            public string Schema;
            public string Name;
        }

        private static SqlServerObjectName ParseSqlServerObjectName(string objectName)
        {
            string value = (objectName ?? string.Empty).Trim();
            int dotIndex = value.IndexOf('.');
            if (dotIndex > 0 && dotIndex < value.Length - 1)
            {
                return new SqlServerObjectName
                {
                    Schema = value.Substring(0, dotIndex).Trim(),
                    Name = value.Substring(dotIndex + 1).Trim()
                };
            }

            return new SqlServerObjectName { Schema = "dbo", Name = value };
        }

        private static string BuildSqlServerQualifiedName(string databaseName, string objectName)
        {
            return BuildSqlServerQualifiedName(databaseName, ParseSqlServerObjectName(objectName));
        }

        private static string BuildSqlServerQualifiedName(string databaseName, SqlServerObjectName objectName)
        {
            return "[" + EscapeSqlServerName(databaseName) + "]." + BuildSqlServerTwoPartName(objectName);
        }

        private static string BuildSqlServerTwoPartName(SqlServerObjectName objectName)
        {
            return "[" + EscapeSqlServerName(objectName.Schema) + "].[" + EscapeSqlServerName(objectName.Name) + "]";
        }

        private static string GetOptionalString(DataRow row, string columnName)
        {
            if (row == null || row.Table == null || !row.Table.Columns.Contains(columnName) || row[columnName] == DBNull.Value) return "";
            return row[columnName].ToString();
        }

        private static int GetOptionalInt(DataRow row, string columnName, int fallback)
        {
            string value = GetOptionalString(row, columnName);
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : fallback;
        }

        private static bool IsCopyNullable(DataRow row)
        {
            return !row.Table.Columns.Contains("IsNullable") || row["IsNullable"] == DBNull.Value || row["IsNullable"].ToString().ToUpper() != "NO";
        }

        private static string MapCopyTypeToSqlServer(DataRow row)
        {
            string type = row["DataType"].ToString().ToLower();
            int length = GetOptionalInt(row, "MaxLength", 0);
            int precision = GetOptionalInt(row, "NumericPrecision", 0);
            int scale = GetOptionalInt(row, "NumericScale", 0);
            if (type.Contains("bigint")) return "BIGINT";
            if (type.Contains("smallint")) return "SMALLINT";
            if (type.Contains("tinyint")) return "TINYINT";
            if (type.Contains("int") || type.Contains("serial")) return "INT";
            if (type.Contains("decimal") || type.Contains("numeric")) return "DECIMAL(" + (precision > 0 ? precision : 38) + "," + (scale >= 0 ? scale : 10) + ")";
            if (type.Contains("money")) return "MONEY";
            if (type.Contains("double") || type.Contains("float")) return "FLOAT";
            if (type.Contains("real")) return "REAL";
            if (type.Contains("bool") || type == "bit") return "BIT";
            if (type == "date") return "DATE";
            if (type == "time") return "TIME";
            if (type.Contains("date") || type.Contains("time")) return "DATETIME2";
            if (type.Contains("varbinary") || type.Contains("binary") || type.Contains("bytea")) return type.Contains("var") ? "VARBINARY(" + FormatSqlServerLength(length) + ")" : "BINARY(" + (length > 0 ? length.ToString() : "1") + ")";
            if (type.Contains("blob") || type.Contains("image")) return "VARBINARY(MAX)";
            if (type.Contains("nchar")) return type.Contains("var") ? "NVARCHAR(" + FormatSqlServerLength(length) + ")" : "NCHAR(" + (length > 0 ? length.ToString() : "1") + ")";
            if (type.Contains("char") || type.Contains("text")) return type.Contains("var") || type.Contains("text") ? "NVARCHAR(" + FormatSqlServerLength(length) + ")" : "NCHAR(" + (length > 0 ? length.ToString() : "1") + ")";
            return "NVARCHAR(MAX)";
        }

        private static string FormatSqlServerLength(int length)
        {
            return length > 0 ? length.ToString() : "MAX";
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
                output["reason"] = ExceptionMessageService.GetReason(ex);
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
                output["reason"] = ExceptionMessageService.GetReason(ex);
                return output;
            }
        }

    }
}
