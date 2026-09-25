using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;

namespace mySQLPunk.lib
{
    public enum DataRowChangeKind
    {
        Insert,
        Update,
        Delete
    }

    public sealed class DataRowChange
    {
        public DataRowChangeKind Kind { get; set; }
        public string KeyText { get; set; }
        /// <summary>Insert：所有同步欄位；Update：只含不同的欄位（值為來源原始物件）。</summary>
        public Dictionary<string, object> Values { get; set; }
        /// <summary>Update／Delete：比對當下讀到的目標整列，用於交易內重新確認與主鍵條件。</summary>
        public Dictionary<string, object> TargetOriginal { get; set; }
    }

    public sealed class DataTableComparison
    {
        public DataTableComparison()
        {
            KeyColumns = new List<string>();
            SyncColumns = new List<string>();
            Changes = new List<DataRowChange>();
            Warnings = new List<string>();
        }

        public string TableName { get; set; }
        public List<string> KeyColumns { get; private set; }
        public List<string> SyncColumns { get; private set; }
        public List<DataRowChange> Changes { get; private set; }
        public List<string> Warnings { get; private set; }
        public int IdenticalRows { get; set; }
        public int SourceRows { get; set; }
        public int TargetRows { get; set; }
        public string SkippedReason { get; set; }

        public bool IsSkipped { get { return SkippedReason != null; } }
        public int Inserts { get { return Changes.Count(item => item.Kind == DataRowChangeKind.Insert); } }
        public int Updates { get { return Changes.Count(item => item.Kind == DataRowChangeKind.Update); } }
        public int Deletes { get { return Changes.Count(item => item.Kind == DataRowChangeKind.Delete); } }

        public string StatusText
        {
            get
            {
                if (SkippedReason != null) return Localization.Format("DataSync.Status.Skipped", SkippedReason);
                return Changes.Count == 0
                    ? Localization.Format("DataSync.Status.Identical", IdenticalRows)
                    : Localization.Format("DataSync.Status.Changes", Inserts, Deletes, Updates, IdenticalRows);
            }
        }
    }

    public sealed class DataSyncTableRequest
    {
        public string TableName { get; set; }
        public List<string> KeyColumns { get; set; }
        public List<DataRowChange> Changes { get; set; }
        /// <summary>寫入明確 identity 值時需要的前後語句與 INSERT 子句（由 provider 包裝層提供）。</summary>
        public string IdentityInsertOn { get; set; }
        public string IdentityInsertOff { get; set; }
        public string InsertClause { get; set; }
        public List<string> AfterInsertStatements { get; set; }
    }

    public sealed class DataSyncResult
    {
        public bool Succeeded { get; set; }
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        public string FailedTable { get; set; }
        public string FailedKey { get; set; }
        public string Message { get; set; }

        public string Summary
        {
            get
            {
                return Succeeded
                    ? Localization.Format("DataSync.Result.Succeeded", Inserted, Updated, Deleted)
                    : Localization.Format("DataSync.Result.Failed", FailedTable, FailedKey, Message);
            }
        }
    }

    /// <summary>
    /// 資料同步的純核心：以 Primary Key 比對兩份 DataTable（同一種 provider 讀出的原始 CLR 值），
    /// 並在單一交易中以參數化語句套用。修改與刪除前會在交易內重新讀取該列並與比對當下的值比較，
    /// 不一致即整批回滾；不依賴資料庫對各型別的等號比較。
    /// </summary>
    public static class DataSyncCore
    {
        public const int DefaultMaximumRows = 50000;
        private const char KeySeparator = '\u0001';

        public static DataTableComparison Compare(string tableName, DataTable source, DataTable target, IList<string> keyColumns, IList<string> writableColumns)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (target == null) throw new ArgumentNullException("target");
            DataTableComparison result = new DataTableComparison { TableName = tableName, SourceRows = source.Rows.Count, TargetRows = target.Rows.Count };
            if (keyColumns == null || keyColumns.Count == 0)
            {
                result.SkippedReason = Localization.T("DataSync.Skip.NoPrimaryKey");
                return result;
            }

            foreach (string key in keyColumns)
            {
                if (!source.Columns.Contains(key) || !target.Columns.Contains(key))
                {
                    result.SkippedReason = Localization.Format("DataSync.Skip.KeyMissing", key);
                    return result;
                }
            }

            List<string> common = source.Columns.Cast<DataColumn>()
                .Select(column => column.ColumnName)
                .Where(name => target.Columns.Contains(name))
                .ToList();
            foreach (DataColumn column in source.Columns)
            {
                if (!target.Columns.Contains(column.ColumnName))
                {
                    result.Warnings.Add(Localization.Format("DataSync.Warn.SourceOnlyColumn", column.ColumnName));
                }
            }

            HashSet<string> writable = new HashSet<string>(writableColumns ?? common, StringComparer.OrdinalIgnoreCase);
            foreach (string name in common.Where(name => !writable.Contains(name) && !keyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                result.Warnings.Add(Localization.Format("DataSync.Warn.ComputedColumn", name));
            }

            List<string> sync = common.Where(name => writable.Contains(name) || keyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
            result.KeyColumns.AddRange(keyColumns);
            result.SyncColumns.AddRange(sync);

            Dictionary<string, DataRow> targetByKey = new Dictionary<string, DataRow>(StringComparer.Ordinal);
            foreach (DataRow row in target.Rows)
            {
                string key = KeyText(row, keyColumns);
                if (targetByKey.ContainsKey(key))
                {
                    result.SkippedReason = Localization.Format("DataSync.Skip.DuplicateKey", DisplayKey(key));
                    return result;
                }
                targetByKey[key] = row;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (DataRow sourceRow in source.Rows)
            {
                string key = KeyText(sourceRow, keyColumns);
                if (!seen.Add(key))
                {
                    result.SkippedReason = Localization.Format("DataSync.Skip.DuplicateKey", DisplayKey(key));
                    return result;
                }

                DataRow targetRow;
                if (!targetByKey.TryGetValue(key, out targetRow))
                {
                    result.Changes.Add(new DataRowChange
                    {
                        Kind = DataRowChangeKind.Insert,
                        KeyText = key,
                        Values = sync.ToDictionary(name => name, name => sourceRow[name], StringComparer.OrdinalIgnoreCase)
                    });
                    continue;
                }

                List<string> changed = sync
                    .Where(name => !keyColumns.Contains(name, StringComparer.OrdinalIgnoreCase) && !ValuesEqual(sourceRow[name], targetRow[name]))
                    .ToList();
                if (changed.Count == 0)
                {
                    result.IdenticalRows++;
                    continue;
                }

                result.Changes.Add(new DataRowChange
                {
                    Kind = DataRowChangeKind.Update,
                    KeyText = key,
                    Values = changed.ToDictionary(name => name, name => sourceRow[name], StringComparer.OrdinalIgnoreCase),
                    TargetOriginal = RowToDictionary(targetRow, sync)
                });
            }

            foreach (KeyValuePair<string, DataRow> entry in targetByKey)
            {
                if (seen.Contains(entry.Key)) continue;
                result.Changes.Add(new DataRowChange
                {
                    Kind = DataRowChangeKind.Delete,
                    KeyText = entry.Key,
                    Values = new Dictionary<string, object>(),
                    TargetOriginal = RowToDictionary(entry.Value, sync)
                });
            }

            return result;
        }

        /// <summary>
        /// 在已開啟的連線上以單一交易套用。tables 需依相依順序（被參照的表在前）：先反向刪除，再依序修改與新增。
        /// quote／qualify 由呼叫端提供；configureParameter 讓 provider 調整參數型別。
        /// </summary>
        public static DataSyncResult Apply(
            DbConnection connection,
            IList<DataSyncTableRequest> tables,
            Func<string, string> quoteIdentifier,
            Func<string, string> qualifyTable,
            string parameterPrefix,
            Action<DbParameter, object> configureParameter)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            if (tables == null) throw new ArgumentNullException("tables");
            if (connection.State != ConnectionState.Open) connection.Open();
            DataSyncResult result = new DataSyncResult();
            string currentTable = null;
            string currentKey = null;
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                try
                {
                    foreach (DataSyncTableRequest table in tables.Reverse())
                    {
                        currentTable = table.TableName;
                        foreach (DataRowChange change in table.Changes.Where(item => item.Kind == DataRowChangeKind.Delete))
                        {
                            currentKey = DisplayKey(change.KeyText);
                            EnsureUnchanged(connection, transaction, table, change, quoteIdentifier, qualifyTable, parameterPrefix, configureParameter);
                            Execute(connection, transaction,
                                "DELETE FROM " + qualifyTable(table.TableName) + " WHERE " + KeyPredicate(table.KeyColumns, quoteIdentifier, parameterPrefix),
                                KeyParameters(table.KeyColumns, change.TargetOriginal), parameterPrefix, configureParameter, 1);
                            result.Deleted++;
                        }
                    }

                    foreach (DataSyncTableRequest table in tables)
                    {
                        currentTable = table.TableName;
                        foreach (DataRowChange change in table.Changes.Where(item => item.Kind == DataRowChangeKind.Update))
                        {
                            currentKey = DisplayKey(change.KeyText);
                            EnsureUnchanged(connection, transaction, table, change, quoteIdentifier, qualifyTable, parameterPrefix, configureParameter);
                            List<string> names = change.Values.Keys.ToList();
                            List<KeyValuePair<string, object>> parameters = names.Select((name, index) => new KeyValuePair<string, object>("v" + index, change.Values[name])).ToList();
                            parameters.AddRange(KeyParameters(table.KeyColumns, change.TargetOriginal));
                            Execute(connection, transaction,
                                "UPDATE " + qualifyTable(table.TableName) + " SET " +
                                string.Join(", ", names.Select((name, index) => quoteIdentifier(name) + " = " + parameterPrefix + "v" + index)) +
                                " WHERE " + KeyPredicate(table.KeyColumns, quoteIdentifier, parameterPrefix),
                                parameters, parameterPrefix, configureParameter, 1);
                            result.Updated++;
                        }

                        List<DataRowChange> inserts = table.Changes.Where(item => item.Kind == DataRowChangeKind.Insert).ToList();
                        if (inserts.Count == 0) continue;
                        if (!string.IsNullOrEmpty(table.IdentityInsertOn)) Execute(connection, transaction, table.IdentityInsertOn, null, parameterPrefix, configureParameter, -1);
                        foreach (DataRowChange change in inserts)
                        {
                            currentKey = DisplayKey(change.KeyText);
                            List<string> names = change.Values.Keys.ToList();
                            Execute(connection, transaction,
                                "INSERT INTO " + qualifyTable(table.TableName) + " (" + string.Join(", ", names.Select(quoteIdentifier)) + ")" +
                                (table.InsertClause ?? string.Empty) + " VALUES (" +
                                string.Join(", ", names.Select((name, index) => parameterPrefix + "v" + index)) + ")",
                                names.Select((name, index) => new KeyValuePair<string, object>("v" + index, change.Values[name])).ToList(),
                                parameterPrefix, configureParameter, 1);
                            result.Inserted++;
                        }

                        if (!string.IsNullOrEmpty(table.IdentityInsertOff)) Execute(connection, transaction, table.IdentityInsertOff, null, parameterPrefix, configureParameter, -1);
                        foreach (string statement in table.AfterInsertStatements ?? new List<string>())
                        {
                            Execute(connection, transaction, statement, null, parameterPrefix, configureParameter, -1);
                        }
                    }

                    transaction.Commit();
                    result.Succeeded = true;
                    return result;
                }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException || ex is StackOverflowException) throw;
                    try { transaction.Rollback(); } catch (DbException) { } catch (InvalidOperationException) { }
                    return new DataSyncResult
                    {
                        Succeeded = false,
                        FailedTable = currentTable,
                        FailedKey = currentKey,
                        Message = SchemaSyncScriptService.SingleLine(ex.Message)
                    };
                }
            }
        }

        public static bool ValuesEqual(object left, object right)
        {
            bool leftNull = left == null || left is DBNull;
            bool rightNull = right == null || right is DBNull;
            if (leftNull || rightNull) return leftNull && rightNull;
            byte[] leftBytes = left as byte[];
            byte[] rightBytes = right as byte[];
            if (leftBytes != null || rightBytes != null) return leftBytes != null && rightBytes != null && leftBytes.SequenceEqual(rightBytes);
            if (IsNumeric(left) && IsNumeric(right))
            {
                try { return Convert.ToDecimal(left, CultureInfo.InvariantCulture) == Convert.ToDecimal(right, CultureInfo.InvariantCulture); }
                catch (OverflowException) { return Convert.ToDouble(left, CultureInfo.InvariantCulture).Equals(Convert.ToDouble(right, CultureInfo.InvariantCulture)); }
            }
            if (left is string && right is string) return string.Equals((string)left, (string)right, StringComparison.Ordinal);
            return left.Equals(right) || string.Equals(Canonical(left), Canonical(right), StringComparison.Ordinal);
        }

        public static string BuildPreviewSql(DataTableComparison comparison, Func<string, string> quoteIdentifier, Func<string, string> qualifyTable, bool includeDeletes)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("-- " + SchemaSyncScriptService.SingleLine(comparison.TableName + "：" + comparison.StatusText));
            string table = qualifyTable(comparison.TableName);
            foreach (DataRowChange change in comparison.Changes)
            {
                string predicate = string.Join(" AND ", comparison.KeyColumns.Select(key =>
                    quoteIdentifier(key) + " = " + Literal(change.TargetOriginal != null ? change.TargetOriginal[key] : change.Values[key])));
                switch (change.Kind)
                {
                    case DataRowChangeKind.Insert:
                        text.AppendLine("INSERT INTO " + table + " (" + string.Join(", ", change.Values.Keys.Select(quoteIdentifier)) + ") VALUES (" +
                            string.Join(", ", change.Values.Values.Select(Literal)) + ");");
                        break;
                    case DataRowChangeKind.Update:
                        text.AppendLine("UPDATE " + table + " SET " + string.Join(", ", change.Values.Select(item => quoteIdentifier(item.Key) + " = " + Literal(item.Value))) +
                            " WHERE " + predicate + ";");
                        break;
                    default:
                        string delete = "DELETE FROM " + table + " WHERE " + predicate + ";";
                        // 註解行必須是單行，否則主鍵字串裡的換行會讓 DELETE 逃出註解。
                        text.AppendLine(includeDeletes ? delete : "-- " + SchemaSyncScriptService.SingleLine(delete));
                        break;
                }
            }
            return text.ToString();
        }

        private static void EnsureUnchanged(
            DbConnection connection,
            DbTransaction transaction,
            DataSyncTableRequest table,
            DataRowChange change,
            Func<string, string> quoteIdentifier,
            Func<string, string> qualifyTable,
            string parameterPrefix,
            Action<DbParameter, object> configureParameter)
        {
            List<string> columns = change.TargetOriginal.Keys.ToList();
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT " + string.Join(", ", columns.Select(quoteIdentifier)) + " FROM " + qualifyTable(table.TableName) +
                                      " WHERE " + KeyPredicate(table.KeyColumns, quoteIdentifier, parameterPrefix);
                AddParameters(command, KeyParameters(table.KeyColumns, change.TargetOriginal), parameterPrefix, configureParameter);
                using (DbDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidOperationException(Localization.T("DataSync.Error.RowChanged"));
                    for (int index = 0; index < columns.Count; index++)
                    {
                        if (!ValuesEqual(reader.GetValue(index), change.TargetOriginal[columns[index]]))
                        {
                            throw new InvalidOperationException(Localization.T("DataSync.Error.RowChanged"));
                        }
                    }
                    if (reader.Read()) throw new InvalidOperationException(Localization.T("DataSync.Error.RowChanged"));
                }
            }
        }

        private static void Execute(
            DbConnection connection,
            DbTransaction transaction,
            string sql,
            IList<KeyValuePair<string, object>> parameters,
            string parameterPrefix,
            Action<DbParameter, object> configureParameter,
            int expectedRows)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                if (parameters != null) AddParameters(command, parameters, parameterPrefix, configureParameter);
                int affected = command.ExecuteNonQuery();
                if (expectedRows >= 0 && affected != expectedRows)
                {
                    throw new InvalidOperationException(Localization.Format("DataSync.Error.AffectedRows", affected));
                }
            }
        }

        private static void AddParameters(DbCommand command, IEnumerable<KeyValuePair<string, object>> parameters, string parameterPrefix, Action<DbParameter, object> configureParameter)
        {
            foreach (KeyValuePair<string, object> item in parameters)
            {
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = parameterPrefix + item.Key;
                parameter.Value = item.Value ?? DBNull.Value;
                if (configureParameter != null) configureParameter(parameter, item.Value);
                command.Parameters.Add(parameter);
            }
        }

        private static string KeyPredicate(IList<string> keys, Func<string, string> quoteIdentifier, string parameterPrefix)
        {
            return string.Join(" AND ", keys.Select((key, index) => quoteIdentifier(key) + " = " + parameterPrefix + "k" + index));
        }

        private static List<KeyValuePair<string, object>> KeyParameters(IList<string> keys, Dictionary<string, object> row)
        {
            return keys.Select((key, index) => new KeyValuePair<string, object>("k" + index, row[key])).ToList();
        }

        private static Dictionary<string, object> RowToDictionary(DataRow row, IEnumerable<string> columns)
        {
            return columns.ToDictionary(name => name, name => row[name], StringComparer.OrdinalIgnoreCase);
        }

        private static string KeyText(DataRow row, IEnumerable<string> keys)
        {
            return string.Join(KeySeparator.ToString(), keys.Select(key => Canonical(row[key])));
        }

        private static string DisplayKey(string key)
        {
            return SchemaSyncScriptService.SingleLine((key ?? string.Empty).Replace(KeySeparator, ','));
        }

        private static bool IsNumeric(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint ||
                   value is long || value is ulong || value is decimal || value is float || value is double;
        }

        private static string Canonical(object value)
        {
            if (value == null || value is DBNull) return "\u0000NULL";
            byte[] bytes = value as byte[];
            if (bytes != null) return "0x" + BitConverter.ToString(bytes).Replace("-", string.Empty);
            if (value is DateTime) return ((DateTime)value).ToString("o", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset) return ((DateTimeOffset)value).ToString("o", CultureInfo.InvariantCulture);
            if (IsNumeric(value))
            {
                try { return Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture); }
                catch (OverflowException) { return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture); }
            }
            IFormattable formattable = value as IFormattable;
            return formattable != null ? formattable.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
        }

        private static string Literal(object value)
        {
            if (value == null || value is DBNull) return "NULL";
            byte[] bytes = value as byte[];
            if (bytes != null) return "0x" + BitConverter.ToString(bytes).Replace("-", string.Empty);
            if (IsNumeric(value)) return Canonical(value);
            if (value is bool) return (bool)value ? "1" : "0";
            return "'" + Canonical(value).Replace("'", "''") + "'";
        }
    }
}
