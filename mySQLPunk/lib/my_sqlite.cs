using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.SQLite;
using System.Data;
using System.IO;
using utility;
namespace mySQLPunk.lib
{
    public class my_sqlite : IDatabase
    {
        myinclude my = new myinclude();
        public SQLiteConnection MCT = null;
        public SQLiteCommand MC = null;
        public SQLiteParameter PA = null;
        private bool _spatialiteLoadTried = false;

        public ConnectionState State => MCT?.State ?? ConnectionState.Closed;
        public string ProviderName => "sqlite";
        public bool SpatiaLiteEnabled { get; private set; } = false;
        public string SpatiaLiteLoadError { get; private set; } = "";

        public void SetConn(string connection)
        {
            MCT = new SQLiteConnection(connection);
        }
        public void setConn(string connection) => SetConn(connection);

        public void setTimeout(int timeout)
        {
            if (MC != null) MC.CommandTimeout = timeout;
        }

        public void Open()
        {
            if (MCT.State != ConnectionState.Open) MCT.Open();
            TryLoadSpatiaLite();
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
            using (SQLiteCommand cmd = new SQLiteCommand(SQL, MCT))
            {
                foreach (var key in key_value.Keys)
                {
                    cmd.Parameters.Add(new SQLiteParameter("@" + key, key_value[key]));
                }
                using (SQLiteDataReader reader = cmd.ExecuteReader())
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

        public bool HasSpatialMetadata()
        {
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('geometry_columns', 'spatial_ref_sys');");
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) >= 2;
        }

        public void InitSpatialMetadata()
        {
            if (!SpatiaLiteEnabled)
            {
                TryLoadSpatiaLite();
            }
            if (!SpatiaLiteEnabled)
            {
                throw new Exception("SpatiaLite extension 尚未載入：" + SpatiaLiteLoadError);
            }

            using (SQLiteCommand cmd = new SQLiteCommand("SELECT InitSpatialMetaData(1);", MCT))
            {
                cmd.ExecuteNonQuery();
            }
        }

        private void TryLoadSpatiaLite()
        {
            if (_spatialiteLoadTried) return;
            _spatialiteLoadTried = true;
            SpatiaLiteEnabled = false;
            SpatiaLiteLoadError = "";

            try
            {
                string extDir = GetSpatiaLiteRuntimeDir();
                if (!Directory.Exists(extDir))
                {
                    SpatiaLiteLoadError = "找不到 SpatiaLite runtime 目錄：" + extDir;
                    return;
                }

                string dllPath = Path.Combine(extDir, "mod_spatialite.dll");
                if (!File.Exists(dllPath))
                {
                    SpatiaLiteLoadError = "找不到 mod_spatialite.dll：" + dllPath;
                    return;
                }

                AddProcessPath(extDir);
                Environment.SetEnvironmentVariable("PROJ_LIB", extDir, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("PROJ_DATA", extDir, EnvironmentVariableTarget.Process);

                MCT.EnableExtensions(true);
                MCT.LoadExtension(dllPath);
                SpatiaLiteEnabled = true;
            }
            catch (Exception ex)
            {
                SpatiaLiteLoadError = ex.Message;
            }
        }

        public static string GetSpatiaLiteRuntimeDir()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "binary", "sqlite3_ext");
        }

        private static void AddProcessPath(string dir)
        {
            string path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? "";
            string[] parts = path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (string.Equals(part.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            Environment.SetEnvironmentVariable("PATH", dir + ";" + path, EnvironmentVariableTarget.Process);
        }

        public Dictionary<string, string> execSQL_SAFE(string SQL, Dictionary<string, object> m)
        {
            Dictionary<string, string> output = new Dictionary<string, string>();
            try
            {
                using (SQLiteCommand cmd = new SQLiteCommand(SQL, MCT))
                {
                    foreach (var key in m.Keys)
                    {
                        cmd.Parameters.Add(new SQLiteParameter("@" + key, m[key]));
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
            using (SQLiteCommand cmd = new SQLiteCommand(sql, MCT))
            {
                if (parameters != null)
                {
                    foreach (var key in parameters.Keys)
                    {
                        cmd.Parameters.Add(new SQLiteParameter("@" + key, parameters[key]));
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
                using (SQLiteCommand cmd = new SQLiteCommand(sql, MCT))
                {
                    if (parameters != null)
                    {
                        foreach (var key in parameters.Keys)
                        {
                            cmd.Parameters.Add(new SQLiteParameter("@" + key, parameters[key]));
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
            return new List<string> { "main" };
        }

        public List<string> GetTables(string databaseName)
        {
            List<string> tables = new List<string>();
            // 排除系統內部表及 virtual table（virtual table 模組未載入時會異常）
            DataTable dt = SelectSQL(
                "SELECT name FROM sqlite_master WHERE type='table' "
                + "AND name NOT LIKE 'sqlite_%' "
                + "AND (sql IS NULL OR sql NOT LIKE 'CREATE VIRTUAL%');");
            foreach (DataRow row in dt.Rows)
            {
                tables.Add(row[0].ToString());
            }
            return tables;
        }

        public List<string> GetViews(string databaseName)
        {
            List<string> views = new List<string>();
            DataTable dt = SelectSQL("SELECT name FROM sqlite_master WHERE type='view';");
            foreach (DataRow row in dt.Rows)
            {
                views.Add(row[0].ToString());
            }
            return views;
        }

        public DataTable GetColumns(string databaseName, string tableName)
        {
            string safeTable = tableName.Replace("'", "''");
            return SelectSQL($"PRAGMA table_info('{safeTable}');");
        }

        public DataTable GetTableStatus(string databaseName)
        {
            DataTable output = CreateTableStatusSchema();
            // 排除 virtual table：其 sql 欄以 CREATE VIRTUAL TABLE 開頭，模組未載入時會拋出 "no such table"
            DataTable tables = SelectSQL(
                "SELECT name, sql FROM sqlite_master WHERE type='table' "
                + "AND name NOT LIKE 'sqlite_%' "
                + "AND (sql IS NULL OR sql NOT LIKE 'CREATE VIRTUAL%') "
                + "ORDER BY name;");
            foreach (DataRow row in tables.Rows)
            {
                string tableName = row["name"].ToString();
                DataRow nr = output.NewRow();
                nr["Name"] = tableName;
                nr["Auto_increment"] = DBNull.Value;
                nr["Update_time"] = DBNull.Value;
                nr["Create_time"] = DBNull.Value;
                nr["Check_time"] = DBNull.Value;
                nr["Data_length"] = 0L;
                nr["Index_length"] = 0L;
                nr["Max_data_length"] = 0L;
                nr["Data_free"] = 0L;
                nr["Engine"] = "SQLite";
                try { nr["Rows"] = CountRows(databaseName, tableName); }
                catch { nr["Rows"] = -1L; } // 無法計數（如被鎖定、尚未初始化等）時展示 -1
                nr["Comment"] = "";
                nr["Row_format"] = "";
                nr["Collation"] = "";
                nr["Create_options"] = "";
                output.Rows.Add(nr);
            }
            return output;
        }

        public DataTable GetIndexes(string databaseName, string tableName)
        {
            DataTable output = CreateIndexSchema();
            DataTable indexes = SelectSQL("PRAGMA index_list(" + QuoteSqlite(tableName) + ");");
            foreach (DataRow idx in indexes.Rows)
            {
                string indexName = idx["name"].ToString();
                DataTable cols = SelectSQL("PRAGMA index_xinfo(" + QuoteSqlite(indexName) + ");");
                foreach (DataRow col in cols.Rows)
                {
                    if (cols.Columns.Contains("key") && col["key"].ToString() != "1") continue;
                    if (col["name"] == DBNull.Value || string.IsNullOrWhiteSpace(col["name"].ToString())) continue;

                    string columnName = col["name"].ToString();
                    if (cols.Columns.Contains("desc") && col["desc"].ToString() == "1")
                    {
                        columnName += " DESC";
                    }

                    DataRow nr = output.NewRow();
                    nr["Key_name"] = indexName;
                    nr["Column_name"] = columnName;
                    nr["Non_unique"] = idx["unique"].ToString() == "1" ? "0" : "1";
                    nr["Seq_in_index"] = Convert.ToInt32(col["seqno"]) + 1;
                    nr["Index_type"] = "BTREE";
                    nr["Index_comment"] = "";
                    output.Rows.Add(nr);
                }
            }
            return output;
        }

        public Dictionary<string, string> GetDatabaseInfo(string databaseName)
        {
            var output = new Dictionary<string, string>();
            output["character_set"] = "UTF-8";
            output["collation"] = "";

            try
            {
                DataTable dt = SelectSQL("PRAGMA encoding;");
                if (dt.Rows.Count > 0)
                {
                    output["character_set"] = dt.Rows[0][0].ToString();
                }
            }
            catch { }

            return output;
        }
        public string GetTableCreateStatement(string databaseName, string tableName)
        {
            string safeTable = tableName.Replace("'", "''");
            DataTable dt = SelectSQL($"SELECT sql FROM sqlite_master WHERE type='table' AND name='{safeTable}';");
            return dt.Rows.Count > 0 ? dt.Rows[0][0].ToString() : "";
        }

        private static DataTable CreateTableStatusSchema()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("Name");
            dt.Columns.Add("Auto_increment");
            dt.Columns.Add("Update_time");
            dt.Columns.Add("Create_time");
            dt.Columns.Add("Check_time");
            dt.Columns.Add("Data_length", typeof(long));
            dt.Columns.Add("Index_length", typeof(long));
            dt.Columns.Add("Max_data_length", typeof(long));
            dt.Columns.Add("Data_free", typeof(long));
            dt.Columns.Add("Engine");
            dt.Columns.Add("Rows", typeof(long));
            dt.Columns.Add("Comment");
            dt.Columns.Add("Row_format");
            dt.Columns.Add("Collation");
            dt.Columns.Add("Create_options");
            return dt;
        }

        private static DataTable CreateIndexSchema()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("Key_name");
            dt.Columns.Add("Column_name");
            dt.Columns.Add("Non_unique");
            dt.Columns.Add("Seq_in_index", typeof(int));
            dt.Columns.Add("Index_type");
            dt.Columns.Add("Index_comment");
            return dt;
        }

        public bool TableExists(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object> { { "name", tableName } };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;", p);
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) > 0;
        }

        public bool ViewExists(string databaseName, string viewName)
        {
            var p = new Dictionary<string, object> { { "name", viewName } };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM sqlite_master WHERE type='view' AND name=@name;", p);
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) > 0;
        }

        public void RenameTable(string databaseName, string oldTableName, string newTableName)
        {
            ExecOrThrow("ALTER TABLE " + QuoteSqlite(oldTableName) + " RENAME TO " + QuoteSqlite(newTableName) + ";");
        }

        public void RenameView(string databaseName, string oldViewName, string newViewName)
        {
            string sql = GetViewCreateStatement(databaseName, oldViewName);
            if (string.IsNullOrWhiteSpace(sql)) throw new Exception("無法取得 View DDL");
            CreateViewFromStatement(databaseName, newViewName, sql);
            ExecOrThrow("DROP VIEW " + QuoteSqlite(oldViewName) + ";");
        }

        public long CountRows(string databaseName, string tableName)
        {
            try
            {
                DataTable dt = SelectSQL("SELECT COUNT(*) FROM " + QuoteSqlite(tableName) + ";");
                return dt.Rows.Count > 0 ? Convert.ToInt64(dt.Rows[0][0]) : 0;
            }
            catch
            {
                return -1;
            }
        }

        public DataTable GetCopyColumns(string databaseName, string tableName)
        {
            DataTable raw = SelectSQL("PRAGMA table_info(" + QuoteSqlite(tableName) + ");");
            DataTable dt = new DataTable();
            dt.Columns.Add("Name");
            dt.Columns.Add("DataType");
            dt.Columns.Add("IsNullable");
            dt.Columns.Add("MaxLength");
            dt.Columns.Add("NumericPrecision");
            dt.Columns.Add("NumericScale");
            dt.Columns.Add("Comment");
            dt.Columns.Add("OrdinalPosition", typeof(int));
            foreach (DataRow row in raw.Rows)
            {
                DataRow nr = dt.NewRow();
                nr["Name"] = row["name"];
                nr["DataType"] = row["type"];
                nr["IsNullable"] = row["notnull"].ToString() == "1" ? "NO" : "YES";
                nr["OrdinalPosition"] = Convert.ToInt32(row["cid"]) + 1;
                dt.Rows.Add(nr);
            }
            return dt;
        }

        public DataTable GetCopyIndexes(string databaseName, string tableName)
        {
            DataTable output = new DataTable();
            output.Columns.Add("IndexName");
            output.Columns.Add("ColumnName");
            output.Columns.Add("NonUnique");
            output.Columns.Add("SeqInIndex", typeof(int));
            output.Columns.Add("IndexType");

            DataTable indexes = SelectSQL("PRAGMA index_list(" + QuoteSqlite(tableName) + ");");
            foreach (DataRow idx in indexes.Rows)
            {
                string indexName = idx["name"].ToString();
                if (indexName.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase)) continue;
                DataTable cols = SelectSQL("PRAGMA index_info(" + QuoteSqlite(indexName) + ");");
                foreach (DataRow col in cols.Rows)
                {
                    DataRow nr = output.NewRow();
                    nr["IndexName"] = indexName;
                    nr["ColumnName"] = col["name"];
                    nr["NonUnique"] = idx["unique"].ToString() == "1" ? "0" : "1";
                    nr["SeqInIndex"] = Convert.ToInt32(col["seqno"]) + 1;
                    nr["IndexType"] = "BTREE";
                    output.Rows.Add(nr);
                }
            }
            return output;
        }

        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider)
        {
            List<string> defs = new List<string>();
            foreach (DataRow row in sourceColumns.Rows)
            {
                string nullable = IsCopyNullable(row) ? "NULL" : "NOT NULL";
                defs.Add(QuoteSqlite(row["Name"].ToString()) + " " + MapCopyTypeToSqlite(row) + " " + nullable);
            }
            ExecOrThrow("CREATE TABLE " + QuoteSqlite(tableName) + " (" + string.Join(", ", defs.ToArray()) + ");");
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
                    cols.Add(QuoteSqlite(row["ColumnName"].ToString()));
                string targetIndexName = tableName + "_" + indexName;
                string sql = "CREATE " + (unique ? "UNIQUE " : "") + "INDEX " + QuoteSqlite(targetIndexName) + " ON " + QuoteSqlite(tableName) + " (" + string.Join(",", cols.ToArray()) + ");";
                ExecOrThrow(sql);
            }
        }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            return SelectSQL("SELECT * FROM " + QuoteSqlite(tableName) + " LIMIT " + limit + " OFFSET " + offset + ";");
        }

        public void InsertTableBatch(string databaseName, string tableName, DataTable rows)
        {
            if (rows == null || rows.Rows.Count == 0) return;
            List<string> cols = new List<string>();
            foreach (DataColumn col in rows.Columns) cols.Add(QuoteSqlite(col.ColumnName));
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
            string sql = "INSERT INTO " + QuoteSqlite(tableName) + " (" + string.Join(",", cols.ToArray()) + ") VALUES " + string.Join(",", valueGroups.ToArray()) + ";";
            ExecOrThrow(sql, p);
        }

        public string GetViewCreateStatement(string databaseName, string viewName)
        {
            var p = new Dictionary<string, object> { { "name", viewName } };
            DataTable dt = SelectSQL("SELECT sql FROM sqlite_master WHERE type='view' AND name=@name;", p);
            return dt.Rows.Count > 0 ? dt.Rows[0][0].ToString() : "";
        }

        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql)
        {
            string selectSql = ViewSqlDialectConverter.ExtractSelectSql(sourceViewSql);
            if (string.IsNullOrWhiteSpace(selectSql))
            {
                throw new Exception("無法解析 SQLite View DDL");
            }

            string sql = "CREATE VIEW " + QuoteSqlite(viewName) + " AS " + selectSql.Trim().TrimEnd(';') + ";";
            ExecOrThrow(sql);
        }

        private void ExecOrThrow(string sql, Dictionary<string, object> parameters = null)
        {
            var res = ExecSQL(sql, parameters);
            if (!res.ContainsKey("status") || res["status"] != "OK")
                throw new Exception(res.ContainsKey("reason") ? res["reason"] : "SQL 執行失敗");
        }

        private static string QuoteSqlite(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

        private static bool IsCopyNullable(DataRow row)
        {
            return !row.Table.Columns.Contains("IsNullable") || row["IsNullable"] == DBNull.Value || row["IsNullable"].ToString().ToUpper() != "NO";
        }

        private static string MapCopyTypeToSqlite(DataRow row)
        {
            string type = row["DataType"].ToString().ToLower();
            if (type.Contains("int") || type.Contains("serial")) return "INTEGER";
            if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("money") || type.Contains("double") || type.Contains("float") || type.Contains("real")) return "REAL";
            if (type.Contains("bool") || type == "bit") return "INTEGER";
            if (type.Contains("blob") || type.Contains("binary") || type.Contains("bytea") || type.Contains("image")) return "BLOB";
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
                    qa.Add("@" + key);
                }
                string SQL = @"
                INSERT INTO `" + table + @"`" +
                    @"(`"
                        + my.implode("`,`", keys) +
                    @"`)
                VALUES("
                        + my.implode(",", qa) +
                    @")";
                MC = new SQLiteCommand(SQL, MCT);
                foreach (var key in m.Keys)
                {
                    PA = new SQLiteParameter("@" + key, m[key]);
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
                whereSQL = whereSQL.Replace("?", "@");
                List<string> fields = new List<string>();
                foreach (var key in m.Keys)
                {
                    fields.Add("`" + key + "`=@" + key);
                }
                string SQL = @"
                UPDATE `" + table + @"` SET " +
                     my.implode(",", fields) +
                @"
                    WHERE 
                        1=1
                        " + whereSQL + @"
                ";
                MC = new SQLiteCommand(SQL, MCT);
                foreach (var key in m.Keys)
                {
                    PA = new SQLiteParameter("@" + key, m[key]);
                    MC.Parameters.Add(PA);
                }
                foreach (var key in wm.Keys)
                {
                    PA = new SQLiteParameter("@" + key, wm[key]);
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
