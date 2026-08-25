using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace mySQLPunk.lib
{
    public sealed class DataProfileValueBucket
    {
        public object Value { get; set; }
        public long Count { get; set; }
    }

    public sealed class DataProfileColumnResult
    {
        public string ColumnName { get; set; }
        public string DataType { get; set; }
        public long SampleCount { get; set; }
        public long NonNullCount { get; set; }
        public long DistinctCount { get; set; }
        public bool HasDistinctCount { get; set; }
        public object Minimum { get; set; }
        public object Maximum { get; set; }
        public object Average { get; set; }
        public bool HasRange { get; set; }
        public bool HasAverage { get; set; }
        public List<DataProfileValueBucket> TopValues { get; } = new List<DataProfileValueBucket>();
        public List<string> Warnings { get; } = new List<string>();

        public long NullCount
        {
            get { return Math.Max(0L, SampleCount - NonNullCount); }
        }

        public bool IsPartial
        {
            get { return Warnings.Count > 0; }
        }
    }

    public sealed class DataProfileReport
    {
        public string DatabaseName { get; set; }
        public string TableName { get; set; }
        public string ProviderName { get; set; }
        public long TotalRowCount { get; set; }
        public long SampleRowCount { get; set; }
        public int RequestedSampleLimit { get; set; }
        public List<DataProfileColumnResult> Columns { get; } = new List<DataProfileColumnResult>();
    }

    public sealed class DataProfileProgress
    {
        public int CompletedColumns { get; set; }
        public int TotalColumns { get; set; }
        public string ColumnName { get; set; }
    }

    /// <summary>
    /// 跨 provider 的欄位資料分析。預設只掃描指定筆數的樣本，避免直接對大型資料表做完整
    /// GROUP BY；sampleLimit 設為 0 時才分析全表。個別型別不支援 DISTINCT、排序或分組時，
    /// 保留其他統計並把原因放進該欄位的 Warnings，不讓整份報告失敗。
    /// </summary>
    public static class DataProfilingService
    {
        private const int MaximumSampleLimit = 1000000;
        private const int MaximumTopValueLimit = 50;

        public static async Task<DataProfileReport> AnalyzeAsync(
            IDatabase db,
            string databaseName,
            string tableName,
            int sampleLimit,
            int topValueLimit,
            IProgress<DataProfileProgress> progress,
            CancellationToken cancellationToken)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException("Database name is required.", nameof(databaseName));
            if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentException("Table name is required.", nameof(tableName));

            sampleLimit = NormalizeSampleLimit(sampleLimit);
            topValueLimit = Math.Max(1, Math.Min(MaximumTopValueLimit, topValueLimit));
            cancellationToken.ThrowIfCancellationRequested();

            DataTable metadata = await Task.Run(() => db.GetColumns(databaseName, tableName), cancellationToken).ConfigureAwait(false);
            List<ColumnMetadata> columns = ReadColumns(metadata);
            if (columns.Count == 0)
            {
                throw new InvalidOperationException("No column metadata was returned for the selected table.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            long totalRows;
            try
            {
                totalRows = await Task.Run(() => db.CountRows(databaseName, tableName), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 有些帳號能 SELECT 但不能讀取 provider 的列數 metadata；分析本身仍可繼續。
                totalRows = -1L;
            }
            string sourceSql = BuildSampleSourceSql(db, databaseName, tableName, sampleLimit);
            long sampleRows = await ReadScalarLongAsync(db, "SELECT COUNT(*) AS sample_count FROM " + sourceSql + ";", cancellationToken).ConfigureAwait(false);

            DataProfileReport report = new DataProfileReport
            {
                DatabaseName = databaseName,
                TableName = tableName,
                ProviderName = db.ProviderName,
                TotalRowCount = totalRows,
                SampleRowCount = sampleRows,
                RequestedSampleLimit = sampleLimit
            };

            for (int index = 0; index < columns.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ColumnMetadata metadataColumn = columns[index];
                DataProfileColumnResult result = new DataProfileColumnResult
                {
                    ColumnName = metadataColumn.Name,
                    DataType = metadataColumn.DataType,
                    SampleCount = sampleRows
                };

                string quotedColumn = DatabaseDumpService.QuoteIdentifier(db, metadataColumn.Name);
                await LoadCountsAsync(db, sourceSql, quotedColumn, result, cancellationToken).ConfigureAwait(false);
                await LoadRangeAsync(db, sourceSql, quotedColumn, metadataColumn.DataType, result, cancellationToken).ConfigureAwait(false);
                await LoadTopValuesAsync(db, sourceSql, metadataColumn.DataType, topValueLimit, result, cancellationToken).ConfigureAwait(false);
                report.Columns.Add(result);

                if (progress != null)
                {
                    progress.Report(new DataProfileProgress
                    {
                        CompletedColumns = index + 1,
                        TotalColumns = columns.Count,
                        ColumnName = metadataColumn.Name
                    });
                }
            }

            return report;
        }

        public static string BuildSampleSourceSql(IDatabase db, string databaseName, string tableName, int sampleLimit)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            sampleLimit = NormalizeSampleLimit(sampleLimit);
            string table = DatabaseDumpService.BuildQualifiedObjectName(db, databaseName, tableName);
            if (sampleLimit == 0) return table;

            string provider = (db.ProviderName ?? string.Empty).Trim().ToLowerInvariant();
            if (provider == "mssql" || provider == "sqlserver")
            {
                return "(SELECT TOP (" + sampleLimit.ToString(CultureInfo.InvariantCulture) + ") * FROM " + table + ") profile_source";
            }
            if (provider == "oracle")
            {
                return "(SELECT * FROM " + table + " WHERE ROWNUM <= " + sampleLimit.ToString(CultureInfo.InvariantCulture) + ") profile_source";
            }
            return "(SELECT * FROM " + table + " LIMIT " + sampleLimit.ToString(CultureInfo.InvariantCulture) + ") profile_source";
        }

        public static string BuildTopValuesSql(IDatabase db, string sourceSql, string columnName, int topValueLimit)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(sourceSql)) throw new ArgumentException("Source SQL is required.", nameof(sourceSql));

            topValueLimit = Math.Max(1, Math.Min(MaximumTopValueLimit, topValueLimit));
            string column = DatabaseDumpService.QuoteIdentifier(db, columnName);
            string provider = (db.ProviderName ?? string.Empty).Trim().ToLowerInvariant();
            if (provider == "oracle")
            {
                string inner = "SELECT " + column + " AS profile_value, COUNT(*) AS occurrences FROM " + sourceSql +
                               " GROUP BY " + column + " ORDER BY COUNT(*) DESC";
                return "SELECT * FROM (" + inner + ") WHERE ROWNUM <= " +
                       topValueLimit.ToString(CultureInfo.InvariantCulture) + ";";
            }
            string selectPrefix = "SELECT ";
            string suffix = string.Empty;
            if (provider == "mssql" || provider == "sqlserver")
            {
                selectPrefix += "TOP (" + topValueLimit.ToString(CultureInfo.InvariantCulture) + ") ";
            }
            else
            {
                suffix = " LIMIT " + topValueLimit.ToString(CultureInfo.InvariantCulture);
            }

            return selectPrefix + column + " AS profile_value, COUNT(*) AS occurrences FROM " + sourceSql +
                   " GROUP BY " + column + " ORDER BY COUNT(*) DESC" + suffix + ";";
        }

        public static string BuildDrilldownSql(
            IDatabase db,
            string databaseName,
            string tableName,
            string columnName,
            object value,
            int rowLimit)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            rowLimit = Math.Max(1, Math.Min(10000, rowLimit));
            string table = DatabaseDumpService.BuildQualifiedObjectName(db, databaseName, tableName);
            string column = DatabaseDumpService.QuoteIdentifier(db, columnName);
            string predicate = value == null || value == DBNull.Value
                ? column + " IS NULL"
                : column + " = " + DatabaseDumpService.ToSqlLiteral(db, value);
            string provider = (db.ProviderName ?? string.Empty).Trim().ToLowerInvariant();

            if (provider == "mssql" || provider == "sqlserver")
            {
                return "SELECT TOP (" + rowLimit.ToString(CultureInfo.InvariantCulture) + ") * FROM " + table + " WHERE " + predicate + ";";
            }
            if (provider == "oracle")
            {
                return "SELECT * FROM " + table + " WHERE " + predicate + " AND ROWNUM <= " + rowLimit.ToString(CultureInfo.InvariantCulture) + ";";
            }
            return "SELECT * FROM " + table + " WHERE " + predicate + " LIMIT " + rowLimit.ToString(CultureInfo.InvariantCulture) + ";";
        }

        private static async Task LoadCountsAsync(
            IDatabase db,
            string sourceSql,
            string quotedColumn,
            DataProfileColumnResult result,
            CancellationToken cancellationToken)
        {
            string fullSql = "SELECT COUNT(" + quotedColumn + ") AS non_null_count, COUNT(DISTINCT " + quotedColumn +
                             ") AS distinct_count FROM " + sourceSql + ";";
            try
            {
                DataTable values = await db.SelectSQLAsync(fullSql).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                DataRow row = FirstRow(values);
                result.NonNullCount = ToLong(row[0]);
                result.DistinctCount = ToLong(row[1]);
                result.HasDistinctCount = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Warnings.Add(ex.Message);
                string fallbackSql = "SELECT COUNT(" + quotedColumn + ") AS non_null_count FROM " + sourceSql + ";";
                DataTable fallback = await db.SelectSQLAsync(fallbackSql).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                result.NonNullCount = ToLong(FirstRow(fallback)[0]);
            }
        }

        private static async Task LoadRangeAsync(
            IDatabase db,
            string sourceSql,
            string quotedColumn,
            string dataType,
            DataProfileColumnResult result,
            CancellationToken cancellationToken)
        {
            if (!SupportsOrdering(dataType)) return;

            bool numeric = IsNumericType(dataType);
            string sql = "SELECT MIN(" + quotedColumn + ") AS minimum_value, MAX(" + quotedColumn + ") AS maximum_value" +
                         (numeric ? ", AVG(" + quotedColumn + ") AS average_value" : string.Empty) +
                         " FROM " + sourceSql + ";";
            try
            {
                DataTable values = await db.SelectSQLAsync(sql).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                DataRow row = FirstRow(values);
                result.Minimum = NormalizeDbNull(row[0]);
                result.Maximum = NormalizeDbNull(row[1]);
                result.HasRange = true;
                if (numeric && row.ItemArray.Length > 2)
                {
                    result.Average = NormalizeDbNull(row[2]);
                    result.HasAverage = true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Warnings.Add(ex.Message);
            }
        }

        private static async Task LoadTopValuesAsync(
            IDatabase db,
            string sourceSql,
            string dataType,
            int topValueLimit,
            DataProfileColumnResult result,
            CancellationToken cancellationToken)
        {
            if (!SupportsGrouping(dataType)) return;

            string sql = BuildTopValuesSql(db, sourceSql, result.ColumnName, topValueLimit);
            try
            {
                DataTable values = await db.SelectSQLAsync(sql).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (values == null) return;
                foreach (DataRow row in values.Rows)
                {
                    if (row.ItemArray.Length < 2) continue;
                    result.TopValues.Add(new DataProfileValueBucket
                    {
                        Value = NormalizeDbNull(row[0]),
                        Count = ToLong(row[1])
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Warnings.Add(ex.Message);
            }
        }

        private static async Task<long> ReadScalarLongAsync(IDatabase db, string sql, CancellationToken cancellationToken)
        {
            DataTable value = await db.SelectSQLAsync(sql).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ToLong(FirstRow(value)[0]);
        }

        private static DataRow FirstRow(DataTable table)
        {
            if (table == null || table.Rows.Count == 0)
            {
                throw new InvalidOperationException("The profiling query returned no rows.");
            }
            return table.Rows[0];
        }

        private static List<ColumnMetadata> ReadColumns(DataTable metadata)
        {
            List<ColumnMetadata> output = new List<ColumnMetadata>();
            if (metadata == null) return output;

            foreach (DataRow row in metadata.Rows)
            {
                string name = ReadMetadataValue(row, "Field", "COLUMN_NAME", "column_name", "Name", "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                string dataType = ReadMetadataValue(row, "Type", "DATA_TYPE", "data_type", "type");
                output.Add(new ColumnMetadata { Name = name, DataType = dataType });
            }
            return output;
        }

        private static string ReadMetadataValue(DataRow row, params string[] names)
        {
            foreach (string name in names)
            {
                DataColumn column = row.Table.Columns.Cast<DataColumn>()
                    .FirstOrDefault(candidate => string.Equals(candidate.ColumnName, name, StringComparison.OrdinalIgnoreCase));
                if (column == null) continue;
                object value = row[column];
                return value == null || value == DBNull.Value ? string.Empty : value.ToString();
            }
            return string.Empty;
        }

        private static bool SupportsOrdering(string dataType)
        {
            string normalized = (dataType ?? string.Empty).ToLowerInvariant();
            if (IsNumericType(normalized)) return true;
            string[] unsupported =
            {
                "blob", "binary", "varbinary", "bytea", "image", "clob", "nclob", "json", "xml",
                "geometry", "geography", "spatial", "array", "object", "cursor"
            };
            return !unsupported.Any(normalized.Contains);
        }

        private static bool SupportsGrouping(string dataType)
        {
            return SupportsOrdering(dataType);
        }

        private static bool IsNumericType(string dataType)
        {
            string normalized = (dataType ?? string.Empty).ToLowerInvariant();
            string[] numeric = { "int", "decimal", "numeric", "number", "real", "double", "float", "money", "serial" };
            return numeric.Any(normalized.Contains) && !normalized.Contains("interval");
        }

        private static int NormalizeSampleLimit(int sampleLimit)
        {
            if (sampleLimit <= 0) return 0;
            return Math.Min(MaximumSampleLimit, sampleLimit);
        }

        private static object NormalizeDbNull(object value)
        {
            return value == null || value == DBNull.Value ? null : value;
        }

        private static long ToLong(object value)
        {
            if (value == null || value == DBNull.Value) return 0L;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private sealed class ColumnMetadata
        {
            public string Name { get; set; }
            public string DataType { get; set; }
        }
    }
}
