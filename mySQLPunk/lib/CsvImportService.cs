using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace mySQLPunk.lib
{
    public sealed class CsvImportResult
    {
        public long Rows { get; set; }
        public List<string> Columns { get; set; }
    }

    /// <summary>
    /// CSV 匯入：RFC 4180 解析（引號、跳脫引號、欄位內換行），第一列為欄位名稱時依名稱（不分大小寫）對應目標欄位，
    /// 值依目標欄位型別轉換後分批寫入。欄位名稱對不上或值無法轉換時在寫入該批前就失敗並指出列號。
    /// </summary>
    public static class CsvImportService
    {
        public const int BatchSize = 500;
        public const int MaximumFieldLength = 1024 * 1024;
        /// <summary>匯入中途失敗時，例外的 Data 會帶已寫入的列數，呼叫端據此避免重試造成重複。</summary>
        public const string ImportedRowsKey = "mySQLPunk.ImportedRows";

        public static IEnumerable<List<string>> Parse(TextReader reader, char delimiter)
        {
            List<string> row = new List<string>();
            StringBuilder field = new StringBuilder();
            bool quoted = false;
            bool fieldStarted = false;
            int read;
            while ((read = reader.Read()) >= 0)
            {
                char c = (char)read;
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (reader.Peek() == '"')
                        {
                            reader.Read();
                            field.Append('"');
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else if (c == '"' && field.Length == 0 && !fieldStarted)
                {
                    quoted = true;
                    fieldStarted = true;
                }
                else if (c == delimiter)
                {
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && reader.Peek() == '\n') reader.Read();
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    if (!(row.Count == 1 && row[0].Length == 0)) yield return row;
                    row = new List<string>();
                }
                else
                {
                    field.Append(c);
                    fieldStarted = true;
                }

                if (field.Length > MaximumFieldLength) throw new FormatException(Localization.Format("Csv.Error.FieldTooLong", MaximumFieldLength));
            }

            if (quoted) throw new FormatException(Localization.T("Csv.Error.UnclosedQuote"));
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                if (!(row.Count == 1 && row[0].Length == 0)) yield return row;
            }
        }

        /// <summary>把 CSV 附加到既有資料表。沒有標題列時欄位依目標資料表的欄位順序對應。</summary>
        public static CsvImportResult Import(IDatabase database, string databaseName, string table, string path, char delimiter, bool hasHeader, bool emptyAsNull)
        {
            DataTable shape = database.SelectTablePage(databaseName, table, 0, 1);
            DataSyncService.ThrowIfQueryFailed(shape);
            if (shape.Columns.Count == 0) throw new InvalidOperationException(Localization.Format("Csv.Error.NoColumns", table));
            using (StreamReader reader = new StreamReader(path, new UTF8Encoding(false), true))
            {
                List<DataColumn> targets = null;
                DataTable batch = null;
                long rowNumber = 0;
                long imported = 0;
                try
                {
                    foreach (List<string> values in Parse(reader, delimiter))
                    {
                        rowNumber++;
                        if (targets == null)
                        {
                            if (hasHeader)
                            {
                                List<string> unknown = values.Where(name => !shape.Columns.Cast<DataColumn>().Any(column => string.Equals(column.ColumnName, name.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
                                if (unknown.Count > 0) throw new InvalidOperationException(Localization.Format("Csv.Error.UnknownColumns", table, string.Join(", ", unknown)));
                                targets = values.Select(name => shape.Columns.Cast<DataColumn>().First(column => string.Equals(column.ColumnName, name.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
                                if (targets.Select(column => column.ColumnName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
                                {
                                    throw new InvalidOperationException(Localization.T("Csv.Error.DuplicateColumns"));
                                }
                                batch = NewBatch(targets);
                                continue;
                            }
                            targets = shape.Columns.Cast<DataColumn>().Take(values.Count).ToList();
                            batch = NewBatch(targets);
                        }

                        if (values.Count != targets.Count)
                        {
                            throw new FormatException(Localization.Format("Csv.Error.FieldCount", rowNumber, values.Count, targets.Count));
                        }
                        DataRow row = batch.NewRow();
                        for (int index = 0; index < targets.Count; index++)
                        {
                            row[index] = Convert(values[index], targets[index], emptyAsNull, rowNumber);
                        }
                        batch.Rows.Add(row);
                        if (batch.Rows.Count >= BatchSize)
                        {
                            database.InsertTableBatch(databaseName, table, batch);
                            imported += batch.Rows.Count;
                            batch = NewBatch(targets);
                        }
                    }

                    if (batch != null && batch.Rows.Count > 0)
                    {
                        database.InsertTableBatch(databaseName, table, batch);
                        imported += batch.Rows.Count;
                    }
                }
                catch (Exception exception)
                {
                    exception.Data[ImportedRowsKey] = imported;
                    throw;
                }
                return new CsvImportResult { Rows = imported, Columns = targets == null ? new List<string>() : targets.Select(column => column.ColumnName).ToList() };
            }
        }

        private static DataTable NewBatch(List<DataColumn> columns)
        {
            DataTable table = new DataTable();
            foreach (DataColumn column in columns) table.Columns.Add(column.ColumnName, column.DataType == typeof(DBNull) ? typeof(object) : column.DataType);
            return table;
        }

        private static object Convert(string text, DataColumn column, bool emptyAsNull, long rowNumber)
        {
            if (text == null || text.Length == 0 && (emptyAsNull || column.DataType != typeof(string))) return DBNull.Value;
            Type type = column.DataType;
            try
            {
                if (type == typeof(string) || type == typeof(object)) return text;
                if (type == typeof(bool))
                {
                    string lowered = text.Trim().ToLowerInvariant();
                    if (lowered == "1" || lowered == "true" || lowered == "t" || lowered == "yes") return true;
                    if (lowered == "0" || lowered == "false" || lowered == "f" || lowered == "no") return false;
                    throw new FormatException();
                }
                if (type == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces);
                if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
                if (type == typeof(TimeSpan)) return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
                if (type == typeof(Guid)) return Guid.Parse(text);
                if (type == typeof(byte[]))
                {
                    string hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.Substring(2) : text;
                    if (hex.Length % 2 != 0) throw new FormatException();
                    byte[] bytes = new byte[hex.Length / 2];
                    for (int index = 0; index < bytes.Length; index++) bytes[index] = byte.Parse(hex.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return bytes;
                }
                return System.Convert.ChangeType(text.Trim(), type, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is FormatException || exception is InvalidCastException || exception is OverflowException)
            {
                throw new FormatException(Localization.Format("Csv.Error.Value", rowNumber, column.ColumnName, text.Length > 60 ? text.Substring(0, 60) + "…" : text, type.Name));
            }
        }
    }
}
