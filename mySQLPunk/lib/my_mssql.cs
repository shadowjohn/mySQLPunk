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
            DataTable dt = SelectSQL("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE';");
            foreach (DataRow row in dt.Rows)
            {
                tables.Add(row[0].ToString());
            }
            return tables;
        }

        public List<string> GetViews(string databaseName)
        {
            List<string> views = new List<string>();
            DataTable dt = SelectSQL("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'VIEW';");
            foreach (DataRow row in dt.Rows)
            {
                views.Add(row[0].ToString());
            }
            return views;
        }

        public DataTable GetColumns(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object> { { "tableName", tableName } };
            return SelectSQL($"SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @tableName", p);
        }

        public DataTable GetTableStatus(string databaseName) => new DataTable();
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();
        public string GetTableCreateStatement(string databaseName, string tableName) => "";

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
