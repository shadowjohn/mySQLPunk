using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;

namespace mySQLPunk.lib
{
    public static class SqlServerBatchInsert
    {
        public static int RowsPerCommand(int columnCount)
        {
            if (columnCount < 1 || columnCount > 2100)
                throw new ArgumentOutOfRangeException("columnCount");
            return Math.Min(1000, 2100 / columnCount);
        }

        public static void Execute(DbConnection connection, string qualifiedTable, DataTable rows)
        {
            if (rows == null || rows.Rows.Count == 0) return;
            RowsPerCommand(rows.Columns.Count);
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                Execute(connection, qualifiedTable, rows, transaction);
                transaction.Commit();
            }
        }

        public static void Execute(DbConnection connection, string qualifiedTable, DataTable rows, DbTransaction transaction)
        {
            if (transaction == null || transaction.Connection != connection)
                throw new ArgumentException("必須提供同一連線的有效交易。", "transaction");
            if (rows == null || rows.Rows.Count == 0) return;
            int batchSize = RowsPerCommand(rows.Columns.Count);
            List<string> columns = new List<string>();
            foreach (DataColumn column in rows.Columns)
                columns.Add("[" + column.ColumnName.Replace("]", "]]") + "]");
            for (int offset = 0; offset < rows.Rows.Count; offset += batchSize)
            {
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    List<string> groups = new List<string>();
                    int count = Math.Min(batchSize, rows.Rows.Count - offset);
                    for (int rowIndex = 0; rowIndex < count; rowIndex++)
                    {
                        List<string> values = new List<string>();
                        for (int columnIndex = 0; columnIndex < rows.Columns.Count; columnIndex++)
                        {
                            string name = "@p" + rowIndex + "_" + columnIndex;
                            values.Add(name);
                            DbParameter parameter = command.CreateParameter();
                            parameter.ParameterName = name;
                            if (rows.Columns[columnIndex].DataType == typeof(DateTime)) parameter.DbType = DbType.DateTime2;
                            if (rows.Columns[columnIndex].DataType == typeof(TimeSpan)) parameter.DbType = DbType.Time;
                            parameter.Value = rows.Rows[offset + rowIndex][columnIndex];
                            command.Parameters.Add(parameter);
                        }
                        groups.Add("(" + string.Join(",", values) + ")");
                    }
                    command.CommandText = "INSERT INTO " + qualifiedTable + " (" + string.Join(",", columns) + ") VALUES " + string.Join(",", groups) + ";";
                    command.ExecuteNonQuery();
                }
            }
        }
    }
}
