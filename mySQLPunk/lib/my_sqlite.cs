using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.SQLite;
using System.Text.RegularExpressions;
using System.Data;
using System.IO;
using utility;
namespace mySQLPunk.lib
{
    public class my_sqlite : IDatabase
    {
        public const string ColumnCommentTableName = "__mysqlpunk_column_comments";
        public const string QueryErrorExtendedProperty = "QueryError";
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
            // LoadExtension 是連線層級的：換了連線就要重新載入，
            // 否則 SpatiaLiteEnabled 會沿用舊連線的狀態說謊
            _spatialiteLoadTried = false;
            SpatiaLiteEnabled = false;
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
            if (MCT != null && MCT.State != ConnectionState.Closed) MCT.Close();
            // 重新 Open 時要重載 SpatiaLite（extension 隨連線關閉而卸載）
            _spatialiteLoadTried = false;
            SpatiaLiteEnabled = false;
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
            try
            {
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
            }
            catch (Exception ex)
            {
                output.ExtendedProperties[QueryErrorExtendedProperty] = ExceptionMessageService.GetReason(ex);
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
                throw new Exception(Localization.Format("Connection.SpatiaLiteNotLoaded", SpatiaLiteLoadError));
            }

            using (SQLiteCommand cmd = new SQLiteCommand("SELECT InitSpatialMetaData(1);", MCT))
            {
                cmd.ExecuteNonQuery();
            }
        }

        public void RetryLoadSpatiaLite()
        {
            _spatialiteLoadTried = false;
            SpatiaLiteEnabled = false;
            SpatiaLiteLoadError = "";

            if (MCT == null || MCT.State != ConnectionState.Open)
            {
                SpatiaLiteLoadError = Localization.T("Connection.SqliteConnectionNotOpen");
                return;
            }

            TryLoadSpatiaLite();
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
                    SpatiaLiteLoadError = Localization.Format("Connection.SpatiaLiteRuntimeDirectoryMissing", extDir);
                    return;
                }

                string dllPath = Path.Combine(extDir, "mod_spatialite.dll");
                if (!File.Exists(dllPath))
                {
                    SpatiaLiteLoadError = Localization.Format("Connection.SpatiaLiteDllMissing", dllPath);
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
                SpatiaLiteLoadError = ExceptionMessageService.GetReason(ex);
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
            return new List<string> { "main" };
        }

        public List<string> GetTables(string databaseName)
        {
            List<string> tables = new List<string>();
            DataTable dt = SelectSQL(
                "SELECT name FROM sqlite_master WHERE type='table' "
                + "AND name NOT LIKE 'sqlite_%' "
                + "AND name <> '" + ColumnCommentTableName + "';");
            // FTS/RTree 影子表（<virtual_table>_xxx）不列出；
            // virtual table 本體要列出來，否則用精靈建完就永遠看不到、也刪不掉。
            var virtualNames = GetVirtualTableNames();
            foreach (DataRow row in dt.Rows)
            {
                string name = row[0].ToString();
                if (IsShadowTable(name, virtualNames)) continue;
                if (virtualNames.Contains(name) && !IsVirtualTableUsable(name)) continue;
                tables.Add(name);
            }
            return tables;
        }

        /// <summary>
        /// virtual table 的模組（fts5/rtree...）沒被編入 SQLite 時一查就丟例外，
        /// 先用零列查詢探測，探不通的就不列出，避免後續操作連環爆。
        /// </summary>
        private bool IsVirtualTableUsable(string tableName)
        {
            try
            {
                DataTable probe = SelectSQL("SELECT 1 FROM " + QuoteSqlite(tableName) + " LIMIT 0;");
                return !HasQueryError(probe);
            }
            catch
            {
                return false;
            }
        }

        private static string GetVirtualTableModule(object sqlValue)
        {
            string sql = sqlValue == null || sqlValue == DBNull.Value ? "" : sqlValue.ToString();
            Match m = Regex.Match(sql, @"USING\s+([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "VIRTUAL";
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
            DataTable columns = SelectSQL($"PRAGMA table_info('{safeTable}');");
            if (!columns.Columns.Contains("Comment")) columns.Columns.Add("Comment");

            Dictionary<string, string> comments = GetColumnComments(tableName);
            foreach (DataRow row in columns.Rows)
            {
                string columnName = row["name"].ToString();
                string comment;
                row["Comment"] = comments.TryGetValue(columnName, out comment) ? comment : "";
            }
            return columns;
        }

        public DataTable GetTableStatus(string databaseName)
        {
            DataTable output = CreateTableStatusSchema();
            DataTable tables = SelectSQL(
                "SELECT name, sql FROM sqlite_master WHERE type='table' "
                + "AND name NOT LIKE 'sqlite_%' "
                + "AND name <> '" + ColumnCommentTableName + "' "
                + "ORDER BY name;");
            var virtualNames = GetVirtualTableNames();
            foreach (DataRow row in tables.Rows)
            {
                string tableName = row["name"].ToString();
                if (IsShadowTable(tableName, virtualNames)) continue; // FTS/RTree 影子表不列出
                bool isVirtual = virtualNames.Contains(tableName);
                if (isVirtual && !IsVirtualTableUsable(tableName)) continue;
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
                nr["Engine"] = isVirtual ? "SQLite " + GetVirtualTableModule(row["sql"]) : "SQLite";
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

        /// <summary>
        /// 取得所有 virtual table 名稱（FTS / RTree / SpatiaLite 等）。
        /// </summary>
        private HashSet<string> GetVirtualTableNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                DataTable dt = SelectSQL(
                    "SELECT name FROM sqlite_master WHERE type='table' AND sql LIKE 'CREATE VIRTUAL%';");
                foreach (DataRow row in dt.Rows)
                    names.Add(row[0].ToString());
            }
            catch { }
            return names;
        }

        /// <summary>
        /// 判斷 tableName 是否為某個 virtual table 的影子表（格式：&lt;vtab&gt;_xxx）。
        /// </summary>
        // FTS3/4/5 與 R-Tree 的影子表後綴。只認這些，
        // 免得把使用者剛好取名為 <vtab>_archive 的正常資料表也一併藏掉。
        private static readonly string[] ShadowTableSuffixes = new string[]
        {
            "data", "idx", "content", "docsize", "config",   // FTS5
            "segments", "segdir", "stat",                    // FTS3/4
            "node", "rowid", "parent",                       // R-Tree
        };

        private static bool IsShadowTable(string tableName, HashSet<string> virtualNames)
        {
            int idx = tableName.LastIndexOf('_');
            if (idx <= 0) return false;
            string prefix = tableName.Substring(0, idx);
            if (!virtualNames.Contains(prefix)) return false;
            string suffix = tableName.Substring(idx + 1);
            foreach (string known in ShadowTableSuffixes)
            {
                if (string.Equals(suffix, known, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
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
            if (string.IsNullOrWhiteSpace(sql)) throw new Exception(Localization.T("Object.ViewDdlUnavailable"));
            CreateViewFromStatement(databaseName, newViewName, sql);
            ExecOrThrow("DROP VIEW " + QuoteSqlite(oldViewName) + ";");
        }

        public long CountRows(string databaseName, string tableName)
        {
            try
            {
                DataTable dt = SelectSQL("SELECT COUNT(*) FROM " + QuoteSqlite(tableName) + ";");
                if (HasQueryError(dt)) return -1;
                return dt.Rows.Count > 0 ? Convert.ToInt64(dt.Rows[0][0]) : 0;
            }
            catch
            {
                return -1;
            }
        }

        private static bool HasQueryError(DataTable table)
        {
            return table != null && table.ExtendedProperties.ContainsKey(QueryErrorExtendedProperty);
        }

        public DataTable GetCopyColumns(string databaseName, string tableName)
        {
            DataTable raw = SelectSQL("PRAGMA table_info(" + QuoteSqlite(tableName) + ");");
            Dictionary<string, string> comments = GetColumnComments(tableName);
            DataTable dt = new DataTable();
            dt.Columns.Add("Name");
            dt.Columns.Add("DataType");
            dt.Columns.Add("IsNullable");
            dt.Columns.Add("MaxLength");
            dt.Columns.Add("NumericPrecision");
            dt.Columns.Add("NumericScale");
            dt.Columns.Add("Comment");
            dt.Columns.Add("OrdinalPosition", typeof(int));
            dt.Columns.Add("ColumnKey");
            foreach (DataRow row in raw.Rows)
            {
                DataRow nr = dt.NewRow();
                nr["Name"] = row["name"];
                nr["DataType"] = row["type"];
                nr["IsNullable"] = row["notnull"].ToString() == "1" ? "NO" : "YES";
                nr["ColumnKey"] = row["pk"].ToString() != "0" ? "PRI" : "";
                string comment;
                nr["Comment"] = comments.TryGetValue(row["name"].ToString(), out comment) ? comment : "";
                nr["OrdinalPosition"] = Convert.ToInt32(row["cid"]) + 1;
                dt.Rows.Add(nr);
            }
            return dt;
        }

        private Dictionary<string, string> GetColumnComments(string tableName)
        {
            Dictionary<string, string> comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(tableName)) return comments;

            try
            {
                DataTable exists = SelectSQL("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='" + ColumnCommentTableName + "';");
                if (exists.Rows.Count == 0 || Convert.ToInt64(exists.Rows[0][0]) <= 0) return comments;

                DataTable rows = SelectSQL(
                    "SELECT column_name, comment FROM " + QuoteSqlite(ColumnCommentTableName) +
                    " WHERE table_name = '" + EscapeSqliteLiteral(tableName) + "';");
                foreach (DataRow row in rows.Rows)
                {
                    string columnName = row["column_name"].ToString();
                    if (string.IsNullOrWhiteSpace(columnName)) continue;
                    comments[columnName] = row["comment"] == DBNull.Value ? "" : row["comment"].ToString();
                }
            }
            catch
            {
                // 讀取失敗時保留已讀到的部分；清空會讓設計師把空註解寫回而毀掉原資料
            }

            return comments;
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

        public void DropTableForCopy(string databaseName, string tableName)
        {
            ExecOrThrow("DROP TABLE IF EXISTS " + QuoteSqlite(tableName) + ";");
        }

        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider)
        {
            List<string> defs = new List<string>();
            List<string> primaryKeys = new List<string>();
            foreach (DataRow row in sourceColumns.Rows)
            {
                string nullable = IsCopyNullable(row) ? "NULL" : "NOT NULL";
                defs.Add(QuoteSqlite(row["Name"].ToString()) + " " + MapCopyTypeToSqlite(row) + " " + nullable);
                bool isPrimaryKey = row.Table.Columns.Contains("ColumnKey") && row["ColumnKey"] != DBNull.Value &&
                    string.Equals(row["ColumnKey"].ToString(), "PRI", StringComparison.OrdinalIgnoreCase);
                if (isPrimaryKey) primaryKeys.Add(QuoteSqlite(row["Name"].ToString()));
            }
            // 沒帶主鍵的複製表在資料分頁會被判成唯讀，來源有 PK 就要一併帶過來
            if (primaryKeys.Count > 0)
            {
                defs.Add("PRIMARY KEY (" + string.Join(", ", primaryKeys.ToArray()) + ")");
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
            string orderBy = GetPrimaryKeyOrderBy(tableName);
            return SelectSQL("SELECT * FROM " + QuoteSqlite(tableName) + orderBy + " LIMIT " + limit + " OFFSET " + offset + ";");
        }

        /// <summary>OFFSET 分頁沒有 ORDER BY 時列順序不保證穩定，有主鍵就用主鍵排序。</summary>
        private string GetPrimaryKeyOrderBy(string tableName)
        {
            try
            {
                DataTable dt = SelectSQL("PRAGMA table_info(" + QuoteSqlite(tableName) + ");");
                SortedDictionary<int, string> pk = new SortedDictionary<int, string>();
                foreach (DataRow row in dt.Rows)
                {
                    int rank = Convert.ToInt32(row["pk"]);
                    if (rank > 0) pk[rank] = QuoteSqlite(row["name"].ToString());
                }
                if (pk.Count > 0) return " ORDER BY " + string.Join(", ", new List<string>(pk.Values).ToArray());
            }
            catch
            {
            }
            return "";
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
                throw new Exception(Localization.Format("Object.ViewDdlParseFailed", "SQLite"));
            }

            string sql = "CREATE VIEW " + QuoteSqlite(viewName) + " AS " + selectSql.Trim().TrimEnd(';') + ";";
            ExecOrThrow(sql);
        }

        private void ExecOrThrow(string sql, Dictionary<string, object> parameters = null)
        {
            var res = ExecSQL(sql, parameters);
            if (!res.ContainsKey("status") || res["status"] != "OK")
                throw new Exception(DatabaseExecutionResultService.GetFailureReason(res));
        }

        private static string QuoteSqlite(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

        private static string EscapeSqliteLiteral(string value)
        {
            return (value ?? "").Replace("'", "''");
        }

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
                output["reason"] = ExceptionMessageService.GetReason(ex);
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
                output["reason"] = ExceptionMessageService.GetReason(ex);
                return output;
            }
        }
    }
}
