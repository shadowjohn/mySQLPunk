using System;
using System.Data;
using System.Data.Common;
using mySQLPunk.lib;

public static class SqlServerBatchInsertTests
{
    public static void Run(DbConnection connection)
    {
        if (SqlServerBatchInsert.RowsPerCommand(1) != 1000 || SqlServerBatchInsert.RowsPerCommand(2) != 1000 ||
            SqlServerBatchInsert.RowsPerCommand(3) != 700 || SqlServerBatchInsert.RowsPerCommand(2100) != 1)
            throw new Exception("SQL Server batch boundary");
        foreach (int columns in new[] { 0, 2101 })
        {
            try { SqlServerBatchInsert.RowsPerCommand(columns); throw new Exception("Invalid column count accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
        try { SqlServerBatchInsert.Execute(connection, "batch_test", new DataTable(), null); throw new Exception("Missing transaction accepted"); }
        catch (ArgumentException) { }
        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE batch_test (id INTEGER PRIMARY KEY, value TEXT, optional TEXT)";
            command.ExecuteNonQuery();
        }
        DataTable rows = new DataTable();
        rows.Columns.Add("id", typeof(int)); rows.Columns.Add("value", typeof(string)); rows.Columns.Add("optional", typeof(string));
        for (int index = 0; index < 1000; index++) rows.Rows.Add(index, "value", DBNull.Value);
        rows.Rows[999]["id"] = 0;
        try { SqlServerBatchInsert.Execute(connection, "batch_test", rows); throw new Exception("Duplicate accepted"); }
        catch (DbException) { }
        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM batch_test";
            if (Convert.ToInt32(command.ExecuteScalar()) != 0) throw new Exception("Second batch failure did not roll back first batch");
            rows.Rows[999]["id"] = 999;
            SqlServerBatchInsert.Execute(connection, "batch_test", rows);
            if (Convert.ToInt32(command.ExecuteScalar()) != 1000) throw new Exception("Retry duplicated rows");
            foreach (int rowCount in new[] { 699, 700, 701, 1000, 1001 })
            {
                command.CommandText = "DELETE FROM batch_test";
                command.ExecuteNonQuery();
                rows.Clear();
                for (int index = 0; index < rowCount; index++) rows.Rows.Add(index, "value", DBNull.Value);
                SqlServerBatchInsert.Execute(connection, "batch_test", rows);
                command.CommandText = "SELECT COUNT(*) FROM batch_test";
                if (Convert.ToInt32(command.ExecuteScalar()) != rowCount) throw new Exception("Three-column boundary " + rowCount);
            }
            command.CommandText = "CREATE TABLE batch_single (id INTEGER)";
            command.ExecuteNonQuery();
            var single = new DataTable(); single.Columns.Add("id", typeof(int));
            for (int index = 0; index < 1001; index++) single.Rows.Add(index);
            SqlServerBatchInsert.Execute(connection, "batch_single", single);
            command.CommandText = "SELECT COUNT(*) FROM batch_single";
            if (Convert.ToInt32(command.ExecuteScalar()) != 1001) throw new Exception("VALUES 1000-row boundary");
        }
    }
}
