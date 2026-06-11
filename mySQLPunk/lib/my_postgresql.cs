using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Npgsql;
using System.Data;
using utility;
namespace mySQLPunk.lib
{
    public class my_postgresql : IDatabase
    {
        myinclude my = new myinclude();
        public NpgsqlConnection MCT = null;
        public NpgsqlCommand MC = null;
        public NpgsqlParameter PA = null;

        public ConnectionState State => MCT?.State ?? ConnectionState.Closed;
        public string ProviderName => "postgresql";

        public void SetConn(string connection)
        {
            MCT = new NpgsqlConnection(connection);
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
            SQL = SQL.Replace("@", ":");
            DataTable output = new DataTable();
            using (NpgsqlCommand cmd = new NpgsqlCommand(SQL, MCT))
            {
                foreach (var key in key_value.Keys)
                {
                    cmd.Parameters.Add(new NpgsqlParameter(":" + key, key_value[key]));
                }
                using (NpgsqlDataReader reader = cmd.ExecuteReader())
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
                using (NpgsqlCommand cmd = new NpgsqlCommand(SQL, MCT))
                {
                    foreach (var key in m.Keys)
                    {
                        cmd.Parameters.Add(new NpgsqlParameter(":" + key, m[key]));
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
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, MCT))
            {
                if (parameters != null)
                {
                    foreach (var key in parameters.Keys)
                    {
                        // 精確替換參數佔位符，或者強制使用者在 PG 模式下用 :key
                        // 這裡為了保持 IDatabase 統一用 @key 的習慣，我們在內部轉為 :key
                        cmd.Parameters.Add(new NpgsqlParameter(key, parameters[key]));
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
                using (NpgsqlCommand cmd = new NpgsqlCommand(sql, MCT))
                {
                    if (parameters != null)
                    {
                        foreach (var key in parameters.Keys)
                        {
                            cmd.Parameters.Add(new NpgsqlParameter(":" + key, parameters[key]));
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
            // ... 原有邏輯 ...
            List<string> dbs = new List<string>();
            DataTable dt = SelectSQL("SELECT datname AS \"Database\" FROM pg_database WHERE datistemplate = false;");
            foreach (DataRow row in dt.Rows)
            {
                dbs.Add(row[0].ToString());
            }
            return dbs;
        }

        public List<string> GetTables(string databaseName)
        {
            List<string> tables = new List<string>();
            DataTable dt = SelectSQL(@"
                SELECT CASE WHEN table_schema = 'public' THEN table_name ELSE table_schema || '.' || table_name END AS table_name
                FROM information_schema.tables
                WHERE table_schema NOT IN ('pg_catalog', 'information_schema') AND table_type = 'BASE TABLE'
                ORDER BY table_schema, table_name;");
            foreach (DataRow row in dt.Rows)
            {
                tables.Add(row[0].ToString());
            }
            return tables;
        }

        public List<string> GetViews(string databaseName)
        {
            List<string> views = new List<string>();
            DataTable dt = SelectSQL(@"
                SELECT CASE WHEN table_schema = 'public' THEN table_name ELSE table_schema || '.' || table_name END AS table_name
                FROM information_schema.tables
                WHERE table_schema NOT IN ('pg_catalog', 'information_schema') AND table_type = 'VIEW'
                ORDER BY table_schema, table_name;");
            foreach (DataRow row in dt.Rows)
            {
                views.Add(row[0].ToString());
            }
            return views;
        }

        public DataTable GetColumns(string databaseName, string tableName)
        {
            PostgreSqlObjectName target = ParsePostgreSqlObjectName(tableName);
            var p = new Dictionary<string, object> { { "schema", target.Schema }, { "tableName", target.Name } };
            return SelectSQL(@"
                SELECT
                    c.column_name,
                    c.data_type,
                    c.is_nullable,
                    c.column_default,
                    c.character_maximum_length,
                    c.numeric_precision,
                    c.numeric_scale,
                    COALESCE(col_description(cls.oid, a.attnum), '') AS ""Comment""
                FROM information_schema.columns c
                LEFT JOIN pg_namespace n ON n.nspname = c.table_schema
                LEFT JOIN pg_class cls ON cls.relnamespace = n.oid AND cls.relname = c.table_name
                LEFT JOIN pg_attribute a ON a.attrelid = cls.oid AND a.attname = c.column_name AND a.attnum > 0 AND NOT a.attisdropped
                WHERE c.table_schema = :schema AND c.table_name = :tableName
                ORDER BY c.ordinal_position", p);
        }

        public DataTable GetTableStatus(string databaseName)
        {
            return SelectSQL(@"
                SELECT
                    CASE WHEN n.nspname = 'public' THEN c.relname ELSE n.nspname || '.' || c.relname END AS ""Name"",
                    NULL AS ""Auto_increment"",
                    NULL AS ""Update_time"",
                    NULL AS ""Create_time"",
                    NULL AS ""Check_time"",
                    pg_total_relation_size(c.oid) AS ""Data_length"",
                    pg_indexes_size(c.oid) AS ""Index_length"",
                    0 AS ""Max_data_length"",
                    0 AS ""Data_free"",
                    'PostgreSQL' AS ""Engine"",
                    COALESCE(s.n_live_tup, 0) AS ""Rows"",
                    COALESCE(obj_description(c.oid), '') AS ""Comment"",
                    '' AS ""Row_format"",
                    '' AS ""Collation"",
                    '' AS ""Create_options""
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
                WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND c.relkind IN ('r', 'p')
                ORDER BY n.nspname, c.relname;");
        }

        public DataTable GetIndexes(string databaseName, string tableName)
        {
            PostgreSqlObjectName target = ParsePostgreSqlObjectName(tableName);
            var p = new Dictionary<string, object> { { "schema", target.Schema }, { "name", target.Name } };
            return SelectSQL(@"
                SELECT
                    CASE WHEN ix.indisprimary THEN 'PRIMARY' ELSE i.relname END AS ""Key_name"",
                    a.attname AS ""Column_name"",
                    CASE WHEN ix.indisunique THEN 0 ELSE 1 END AS ""Non_unique"",
                    k.ord AS ""Seq_in_index"",
                    am.amname AS ""Index_type"",
                    COALESCE(obj_description(i.oid), '') AS ""Index_comment""
                FROM pg_class t
                JOIN pg_namespace n ON n.oid = t.relnamespace
                JOIN pg_index ix ON t.oid = ix.indrelid
                JOIN pg_class i ON i.oid = ix.indexrelid
                JOIN pg_am am ON i.relam = am.oid
                JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
                WHERE n.nspname = :schema AND t.relname = :name
                ORDER BY i.relname, k.ord;", p);
        }

        public Dictionary<string, string> GetDatabaseInfo(string databaseName)
        {
            var output = new Dictionary<string, string>();
            DataTable dt = SelectSQL(@"
                SELECT
                    pg_encoding_to_char(encoding) AS character_set,
                    datcollate AS collation
                FROM pg_database
                WHERE datname = current_database();");
            if (dt.Rows.Count > 0)
            {
                output["character_set"] = dt.Rows[0]["character_set"].ToString();
                output["collation"] = dt.Rows[0]["collation"].ToString();
            }
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
                definitions.Add("  " + QuotePg(row["Name"].ToString()) + " " + MapCopyTypeToPostgreSql(row) + " " + nullable);
            }

            return "CREATE TABLE " + QualifiedName(tableName) + " (\r\n" +
                   string.Join(",\r\n", definitions.ToArray()) +
                   "\r\n);";
        }

        public bool TableExists(string databaseName, string tableName)
        {
            PostgreSqlObjectName target = ParsePostgreSqlObjectName(tableName);
            var p = new Dictionary<string, object> { { "schema", target.Schema }, { "name", target.Name } };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = :schema AND table_name = :name AND table_type = 'BASE TABLE';", p);
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) > 0;
        }

        public bool ViewExists(string databaseName, string viewName)
        {
            PostgreSqlObjectName target = ParsePostgreSqlObjectName(viewName);
            var p = new Dictionary<string, object> { { "schema", target.Schema }, { "name", target.Name } };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM information_schema.views WHERE table_schema = :schema AND table_name = :name;", p);
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) > 0;
        }

        public void RenameTable(string databaseName, string oldTableName, string newTableName)
        {
            ExecOrThrow("ALTER TABLE " + QualifiedName(oldTableName) + " RENAME TO " + QuotePg(ParsePostgreSqlObjectName(newTableName).Name) + ";");
        }

        public void RenameView(string databaseName, string oldViewName, string newViewName)
        {
            ExecOrThrow("ALTER VIEW " + QualifiedName(oldViewName) + " RENAME TO " + QuotePg(ParsePostgreSqlObjectName(newViewName).Name) + ";");
        }

        public long CountRows(string databaseName, string tableName)
        {
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM " + QualifiedName(tableName) + ";");
            return dt.Rows.Count > 0 ? Convert.ToInt64(dt.Rows[0][0]) : 0;
        }

        public DataTable GetCopyColumns(string databaseName, string tableName)
        {
            PostgreSqlObjectName target = ParsePostgreSqlObjectName(tableName);
            var p = new Dictionary<string, object> { { "schema", target.Schema }, { "name", target.Name } };
            return SelectSQL(@"
                SELECT
                    c.column_name AS ""Name"",
                    c.data_type AS ""DataType"",
                    c.is_nullable AS ""IsNullable"",
                    c.character_maximum_length AS ""MaxLength"",
                    c.numeric_precision AS ""NumericPrecision"",
                    c.numeric_scale AS ""NumericScale"",
                    c.column_default AS ""DefaultValue"",
                    col_description(cls.oid, a.attnum) AS ""Comment"",
                    c.ordinal_position AS ""OrdinalPosition""
                FROM information_schema.columns c
                LEFT JOIN pg_namespace n ON n.nspname = c.table_schema
                LEFT JOIN pg_class cls ON cls.relnamespace = n.oid AND cls.relname = c.table_name
                LEFT JOIN pg_attribute a ON a.attrelid = cls.oid AND a.attname = c.column_name AND a.attnum > 0 AND NOT a.attisdropped
                WHERE c.table_schema = :schema AND c.table_name = :name
                ORDER BY c.ordinal_position;", p);
        }

        public DataTable GetCopyIndexes(string databaseName, string tableName)
        {
            PostgreSqlObjectName target = ParsePostgreSqlObjectName(tableName);
            var p = new Dictionary<string, object> { { "schema", target.Schema }, { "name", target.Name } };
            return SelectSQL(@"
                SELECT
                    i.relname AS ""IndexName"",
                    a.attname AS ""ColumnName"",
                    CASE WHEN ix.indisunique THEN 0 ELSE 1 END AS ""NonUnique"",
                    k.ord AS ""SeqInIndex"",
                    am.amname AS ""IndexType""
                FROM pg_class t
                JOIN pg_namespace n ON n.oid = t.relnamespace
                JOIN pg_index ix ON t.oid = ix.indrelid
                JOIN pg_class i ON i.oid = ix.indexrelid
                JOIN pg_am am ON i.relam = am.oid
                JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
                WHERE n.nspname = :schema AND t.relname = :name AND NOT ix.indisprimary
                ORDER BY i.relname, k.ord;", p);
        }

        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider)
        {
            foreach (string sql in BuildPostgreSqlCopyCreateTableStatements(tableName, sourceColumns, sourceProvider))
            {
                ExecOrThrow(sql);
            }
        }

        private static List<string> BuildPostgreSqlCopyCreateTableStatements(string tableName, DataTable sourceColumns, string sourceProvider)
        {
            List<string> statements = new List<string>();
            List<string> defs = new List<string>();
            foreach (DataRow row in sourceColumns.Rows)
            {
                string nullable = IsCopyNullable(row) ? "NULL" : "NOT NULL";
                string definition = QuotePg(row["Name"].ToString()) + " " + MapCopyTypeToPostgreSql(row);
                string defaultValue = GetPostgreSqlCopyDefaultValue(row, sourceProvider);
                if (!string.IsNullOrWhiteSpace(defaultValue))
                {
                    definition += " DEFAULT " + defaultValue;
                }
                definition += " " + nullable;
                defs.Add(definition);
            }

            statements.Add("CREATE TABLE " + QualifiedName(tableName) + " (" + string.Join(", ", defs.ToArray()) + ");");

            foreach (DataRow row in sourceColumns.Rows)
            {
                string comment = GetDataRowValue(row, "Comment", "COMMENT");
                if (string.IsNullOrWhiteSpace(comment)) continue;
                statements.Add("COMMENT ON COLUMN " + QualifiedName(tableName) + "." + QuotePg(row["Name"].ToString()) +
                               " IS '" + comment.Replace("'", "''") + "';");
            }

            return statements;
        }

        private static string GetPostgreSqlCopyDefaultValue(DataRow row, string sourceProvider)
        {
            if (!string.Equals(sourceProvider, "postgresql", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sourceProvider, "postgres", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            return GetDataRowValue(row, "DefaultValue", "DEFAULTVALUE", "ColumnDefault", "COLUMN_DEFAULT").Trim();
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
                    cols.Add(QuotePg(row["ColumnName"].ToString()));
                string targetIndexName = ParsePostgreSqlObjectName(tableName).Name + "_" + indexName;
                string sql = "CREATE " + (unique ? "UNIQUE " : "") + "INDEX " + QuotePg(targetIndexName) + " ON " + QualifiedName(tableName) + " (" + string.Join(",", cols.ToArray()) + ");";
                ExecOrThrow(sql);
            }
        }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            return SelectSQL("SELECT * FROM " + QualifiedName(tableName) + " LIMIT " + limit + " OFFSET " + offset + ";");
        }

        public void InsertTableBatch(string databaseName, string tableName, DataTable rows)
        {
            if (rows == null || rows.Rows.Count == 0) return;
            List<string> cols = new List<string>();
            foreach (DataColumn col in rows.Columns) cols.Add(QuotePg(col.ColumnName));
            List<string> valueGroups = new List<string>();
            Dictionary<string, object> p = new Dictionary<string, object>();
            for (int r = 0; r < rows.Rows.Count; r++)
            {
                List<string> vals = new List<string>();
                for (int c = 0; c < rows.Columns.Count; c++)
                {
                    string key = "p" + r + "_" + c;
                    vals.Add(":" + key);
                    p[key] = rows.Rows[r][c] == DBNull.Value ? DBNull.Value : rows.Rows[r][c];
                }
                valueGroups.Add("(" + string.Join(",", vals.ToArray()) + ")");
            }
            string sql = "INSERT INTO " + QualifiedName(tableName) + " (" + string.Join(",", cols.ToArray()) + ") VALUES " + string.Join(",", valueGroups.ToArray()) + ";";
            ExecOrThrow(sql, p);
        }

        public string GetViewCreateStatement(string databaseName, string viewName)
        {
            PostgreSqlObjectName target = ParsePostgreSqlObjectName(viewName);
            var p = new Dictionary<string, object> { { "schema", target.Schema }, { "name", target.Name } };
            DataTable dt = SelectSQL("SELECT pg_get_viewdef((quote_ident(:schema) || '.' || quote_ident(:name))::regclass, true);", p);
            return dt.Rows.Count > 0 ? dt.Rows[0][0].ToString() : "";
        }

        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql)
        {
            string selectSql = ViewSqlDialectConverter.ExtractSelectSql(sourceViewSql);
            if (string.IsNullOrWhiteSpace(selectSql))
            {
                throw new Exception(Localization.Format("Object.ViewDdlParseFailed", "PostgreSQL"));
            }

            ExecOrThrow("CREATE VIEW " + QualifiedName(viewName) + " AS " + selectSql.Trim().TrimEnd(';') + ";");
        }

        private void ExecOrThrow(string sql, Dictionary<string, object> parameters = null)
        {
            var res = ExecSQL(sql, parameters);
            if (!res.ContainsKey("status") || res["status"] != "OK")
                throw new Exception(DatabaseExecutionResultService.GetFailureReason(res));
        }

        private struct PostgreSqlObjectName
        {
            public string Schema;
            public string Name;
        }

        private static PostgreSqlObjectName ParsePostgreSqlObjectName(string objectName)
        {
            string value = (objectName ?? string.Empty).Trim();
            int dotIndex = value.IndexOf('.');
            if (dotIndex > 0 && dotIndex < value.Length - 1)
            {
                return new PostgreSqlObjectName
                {
                    Schema = value.Substring(0, dotIndex).Trim(),
                    Name = value.Substring(dotIndex + 1).Trim()
                };
            }

            return new PostgreSqlObjectName { Schema = "public", Name = value };
        }

        private static string QualifiedName(string objectName)
        {
            PostgreSqlObjectName target = ParsePostgreSqlObjectName(objectName);
            return QuotePg(target.Schema) + "." + QuotePg(target.Name);
        }

        private static string QuotePg(string name) => "\"" + (name ?? "").Replace("\"", "\"\"") + "\"";

        private static bool IsCopyNullable(DataRow row)
        {
            return !row.Table.Columns.Contains("IsNullable") || row["IsNullable"] == DBNull.Value || row["IsNullable"].ToString().ToUpper() != "NO";
        }

        private static string GetDataRowValue(DataRow row, params string[] names)
        {
            foreach (string name in names)
            {
                if (row.Table.Columns.Contains(name) && row[name] != DBNull.Value)
                {
                    return row[name].ToString();
                }
            }
            return "";
        }

        private static string MapCopyTypeToPostgreSql(DataRow row)
        {
            string type = row["DataType"].ToString().ToLower();
            if (type.Contains("bigint")) return "BIGINT";
            if (type.Contains("smallint")) return "SMALLINT";
            if (type.Contains("int") || type.Contains("serial")) return "INTEGER";
            if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("money")) return "NUMERIC(38,10)";
            if (type.Contains("double") || type.Contains("float") || type.Contains("real")) return "DOUBLE PRECISION";
            if (type.Contains("bool") || type == "bit") return "BOOLEAN";
            if (type.Contains("date") || type.Contains("time")) return "TIMESTAMP";
            if (type.Contains("blob") || type.Contains("binary") || type.Contains("bytea") || type.Contains("image")) return "BYTEA";
            return "TEXT";
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
                    qa.Add(":" + key);
                }
                string SQL = @"
                INSERT INTO " + QualifiedName(table) +
                    @"("""
                        + my.implode(@""",""", keys) +
                    @""")
                VALUES("
                        + my.implode(",", qa) +
                    @")";
                MC = new NpgsqlCommand(SQL, MCT);
                foreach (var key in m.Keys)
                {
                    PA = new NpgsqlParameter(":" + key, m[key]);
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
                whereSQL = whereSQL.Replace("@", ":");
                List<string> fields = new List<string>();
                foreach (var key in m.Keys)
                {
                    fields.Add(@"""" + key + @"""=:" + key);
                }
                string SQL = @"
                UPDATE " + QualifiedName(table) + @" SET " +
                     my.implode(",", fields) +
                @"
                    WHERE 
                        1=1
                        " + whereSQL + @"
                ";
                MC = new NpgsqlCommand(SQL, MCT);
                foreach (var key in m.Keys)
                {
                    PA = new NpgsqlParameter(":" + key, m[key]);
                    MC.Parameters.Add(PA);
                }
                foreach (var key in wm.Keys)
                {
                    PA = new NpgsqlParameter(":" + key, wm[key]);
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
