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

        public void SetConn(string connection)
        {
            if (!connection.ToLower().Contains("allowzerodatetime"))
            {
                if (!connection.EndsWith(";")) connection += ";";
                connection += "AllowZeroDateTime=True;";
            }
            MCT = new MySqlConnection(connection);
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
            // ... 同原本邏輯 ...
            List<string> dbs = new List<string>();
            DataTable dt = SelectSQL("SHOW DATABASES;");
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
            DataTable dt = SelectSQL($"SHOW TABLES FROM `{safeDB}`;");
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
            DataTable dt = SelectSQL($"SHOW FULL TABLES FROM `{safeDB}` WHERE Table_type = 'VIEW';");
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
            return SelectSQL($"SHOW FULL COLUMNS FROM `{safeDB}`.`{safeTable}`;");
        }

        public DataTable GetIndexes(string databaseName, string tableName)
        {
            string safeDB = databaseName.Replace("`", "``");
            string safeTable = tableName.Replace("`", "``");
            return SelectSQL($"SHOW INDEX FROM `{safeDB}`.`{safeTable}`;");
        }

        public DataTable GetTableStatus(string databaseName)
        {
            string safeDB = databaseName.Replace("`", "``");
            return SelectSQL($"SHOW TABLE STATUS FROM `{safeDB}`;");
        }

        public Dictionary<string, string> GetDatabaseInfo(string databaseName)
        {
            var output = new Dictionary<string, string>();
            var p = new Dictionary<string, object> { { "db", databaseName } };
            DataTable dt = SelectSQL("SELECT default_character_set_name, default_collation_name FROM information_schema.schemata WHERE schema_name = ?db", p);
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