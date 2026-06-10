using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;

namespace mySQLPunk.lib
{
    public static class BinaryCellStreamingService
    {
        public const int DefaultBufferSize = 81920;

        public static long WriteFirstColumnToFile(
            IDatabase database,
            string sql,
            IDictionary<string, object> parameters,
            string targetPath,
            Action<long, long> progress = null,
            int bufferSize = DefaultBufferSize)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException(Localization.T("Common.SqlRequired"), nameof(sql));
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException(Localization.T("Common.TargetPathRequired"), nameof(targetPath));
            if (bufferSize <= 0) bufferSize = DefaultBufferSize;

            DbConnection connection = GetOpenConnection(database);
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                AddParameters(command, database, parameters);

                using (DbDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow))
                {
                    if (!reader.Read() || reader.IsDBNull(0))
                    {
                        File.WriteAllBytes(targetPath, new byte[0]);
                        progress?.Invoke(0, 0);
                        return 0;
                    }

                    using (FileStream file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize))
                    {
                        return WriteReaderColumn(reader, 0, file, progress, bufferSize);
                    }
                }
            }
        }

        public static long WriteBytesToFile(byte[] bytes, string targetPath, Action<long, long> progress = null, int bufferSize = DefaultBufferSize)
        {
            if (bytes == null) bytes = new byte[0];
            if (bufferSize <= 0) bufferSize = DefaultBufferSize;

            using (MemoryStream input = new MemoryStream(bytes, false))
            using (FileStream output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize))
            {
                return CopyStream(input, output, bytes.Length, progress, bufferSize);
            }
        }

        internal static DbConnection GetOpenConnection(IDatabase database)
        {
            DbConnection connection = GetConnection(database);
            if (connection == null)
            {
                throw new NotSupportedException(Localization.T("Query.StreamingConnectionUnavailable"));
            }

            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            return connection;
        }

        private static DbConnection GetConnection(IDatabase database)
        {
            my_mysql mysql = database as my_mysql;
            if (mysql != null) return mysql.MCT;

            my_postgresql postgresql = database as my_postgresql;
            if (postgresql != null) return postgresql.MCT;

            my_mssql mssql = database as my_mssql;
            if (mssql != null) return mssql.MCT;

            my_sqlite sqlite = database as my_sqlite;
            if (sqlite != null) return sqlite.MCT;

            my_oracle oracle = database as my_oracle;
            if (oracle != null) return oracle.MCT;

            return null;
        }

        internal static void AddParameters(DbCommand command, IDatabase database, IDictionary<string, object> parameters)
        {
            if (parameters == null) return;

            foreach (KeyValuePair<string, object> pair in parameters)
            {
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = BuildParameterName(database, pair.Key);
                parameter.Value = pair.Value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }

        internal static string BuildParameterName(IDatabase database, string key)
        {
            string name = (key ?? string.Empty).TrimStart('@', ':', '?');
            if (database is my_mysql) return "?" + name;
            if (database is my_postgresql || database is my_oracle) return ":" + name;
            return "@" + name;
        }

        private static long WriteReaderColumn(DbDataReader reader, int ordinal, Stream output, Action<long, long> progress, int bufferSize)
        {
            long totalLength = TryGetLength(reader, ordinal);
            byte[] buffer = new byte[bufferSize];
            long offset = 0;

            try
            {
                while (true)
                {
                    long read = reader.GetBytes(ordinal, offset, buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    output.Write(buffer, 0, (int)read);
                    offset += read;
                    progress?.Invoke(offset, totalLength);
                }

                return offset;
            }
            catch (InvalidCastException)
            {
                return WriteFallbackValue(reader.GetValue(ordinal), output, progress, bufferSize);
            }
            catch (NotSupportedException)
            {
                return WriteFallbackValue(reader.GetValue(ordinal), output, progress, bufferSize);
            }
        }

        private static long TryGetLength(DbDataReader reader, int ordinal)
        {
            try
            {
                return reader.GetBytes(ordinal, 0, null, 0, 0);
            }
            catch
            {
                return -1;
            }
        }

        private static long WriteFallbackValue(object value, Stream output, Action<long, long> progress, int bufferSize)
        {
            byte[] bytes = value as byte[];
            if (bytes != null)
            {
                using (MemoryStream input = new MemoryStream(bytes, false))
                {
                    return CopyStream(input, output, bytes.Length, progress, bufferSize);
                }
            }

            string text = value == null || value == DBNull.Value ? string.Empty : value.ToString();
            byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text);
            output.Write(textBytes, 0, textBytes.Length);
            progress?.Invoke(textBytes.Length, textBytes.Length);
            return textBytes.Length;
        }

        private static long CopyStream(Stream input, Stream output, long totalLength, Action<long, long> progress, int bufferSize)
        {
            byte[] buffer = new byte[bufferSize];
            long written = 0;
            int read;

            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                written += read;
                progress?.Invoke(written, totalLength);
            }

            if (written == 0)
            {
                progress?.Invoke(0, totalLength);
            }

            return written;
        }
    }
}
