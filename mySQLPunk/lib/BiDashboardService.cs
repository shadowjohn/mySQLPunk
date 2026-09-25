using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace mySQLPunk.lib
{
    public enum BiChartKind
    {
        Bar,
        Line,
        Pie,
        Number,
        Table
    }

    public enum BiAggregate
    {
        Count,
        Sum,
        Average,
        Minimum,
        Maximum,
        DistinctCount
    }

    public enum BiDateGrain
    {
        None,
        Year,
        Month,
        Day
    }

    public sealed class BiCalculatedField
    {
        public string Name { get; set; }
        public string Expression { get; set; }
    }

    /// <summary>儀表板資料集：一段唯讀查詢（MongoDB 為 JSON 查詢），加上以運算式衍生的計算欄位。</summary>
    public sealed class BiDataset
    {
        public BiDataset()
        {
            CalculatedFields = new List<BiCalculatedField>();
        }

        public string Name { get; set; }
        public string Query { get; set; }
        public List<BiCalculatedField> CalculatedFields { get; set; }
    }

    public sealed class BiWidget
    {
        public string Title { get; set; }
        public string Dataset { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public BiChartKind Kind { get; set; }
        /// <summary>分組欄位；數字卡可留空。</summary>
        public string Category { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public BiDateGrain DateGrain { get; set; }
        /// <summary>彙總欄位；Count 可留空（計算列數）。</summary>
        public string Value { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public BiAggregate Aggregate { get; set; }
        /// <summary>只納入運算式為真的列，例如 [status] = 'paid'。</summary>
        public string Filter { get; set; }
        public int TopN { get; set; } = 12;
        /// <summary>true 依數值由大到小；false 依分類排序（折線圖預設）。</summary>
        public bool SortByValue { get; set; } = true;
        /// <summary>點選分類時是否篩選其他圖表，也決定是否接受其他圖表的篩選。</summary>
        public bool CrossFilter { get; set; } = true;
        /// <summary>佔用的欄數：1 或 2。</summary>
        public int Span { get; set; } = 1;
    }

    public sealed class BiDashboard
    {
        public BiDashboard()
        {
            Datasets = new List<BiDataset>();
            Widgets = new List<BiWidget>();
        }

        public int Version { get; set; } = 1;
        public string Title { get; set; }
        public List<BiDataset> Datasets { get; set; }
        public List<BiWidget> Widgets { get; set; }
    }

    /// <summary>跨圖表篩選：某個欄位等於某個分類鍵（鍵以 BiDashboardService.KeyOf 產生）。</summary>
    public sealed class BiFilter
    {
        public BiFilter(int sourceWidget, string field, BiDateGrain grain, string key, string label)
        {
            SourceWidget = sourceWidget;
            Field = field;
            Grain = grain;
            Key = key;
            Label = label;
        }

        public int SourceWidget { get; private set; }
        public string Field { get; private set; }
        public BiDateGrain Grain { get; private set; }
        public string Key { get; private set; }
        public string Label { get; private set; }
    }

    /// <summary>載入並加上計算欄位之後的資料集；值皆已正規化（decimal／string／bool／DateTime／null）。</summary>
    public sealed class BiDatasetData
    {
        public BiDatasetData(List<string> columns, List<object[]> rows, bool truncated)
        {
            Columns = columns;
            Rows = rows;
            Truncated = truncated;
        }

        public List<string> Columns { get; private set; }
        public List<object[]> Rows { get; private set; }
        public bool Truncated { get; private set; }

        public int IndexOf(string column)
        {
            if (string.IsNullOrWhiteSpace(column)) return -1;
            for (int i = 0; i < Columns.Count; i++)
            {
                if (string.Equals(Columns[i], column.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }
    }

    public sealed class BiPoint
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public decimal? Value { get; set; }
    }

    public sealed class BiWidgetResult
    {
        public BiWidgetResult()
        {
            Points = new List<BiPoint>();
        }

        public List<BiPoint> Points { get; private set; }
        public decimal? Total { get; set; }
        public int MatchedRows { get; set; }
        public int GroupCount { get; set; }
        public DataTable Table { get; set; }
        public string Error { get; set; }
    }

    public static class BiDashboardService
    {
        public const string FileExtension = ".punkbi";
        public const int MaximumDatasets = 20;
        public const int MaximumWidgets = 40;
        public const int MaximumCalculatedFields = 40;
        public const int MaximumRows = 200000;
        public const int MaximumTopN = 100;
        public const int MaximumTableRows = 500;
        private const string NullKey = "\u0000null";

        // ------------------------------------------------------------ Model

        public static void Validate(BiDashboard dashboard)
        {
            if (dashboard == null) throw new InvalidOperationException(Localization.T("Bi.Error.Empty"));
            if (dashboard.Version != 1) throw new InvalidOperationException(Localization.Format("Bi.Error.Version", dashboard.Version));
            if (dashboard.Datasets == null) dashboard.Datasets = new List<BiDataset>();
            if (dashboard.Widgets == null) dashboard.Widgets = new List<BiWidget>();
            if (dashboard.Datasets.Count > MaximumDatasets) throw new InvalidOperationException(Localization.Format("Bi.Error.TooManyDatasets", MaximumDatasets));
            if (dashboard.Widgets.Count > MaximumWidgets) throw new InvalidOperationException(Localization.Format("Bi.Error.TooManyWidgets", MaximumWidgets));
            dashboard.Title = (dashboard.Title ?? string.Empty).Trim();

            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BiDataset dataset in dashboard.Datasets) ValidateDataset(dataset, names);

            foreach (BiWidget widget in dashboard.Widgets) ValidateWidget(widget, names);
        }

        public static void ValidateDataset(BiDataset dataset, HashSet<string> existingNames)
        {
            if (dataset == null) throw new InvalidOperationException(Localization.T("Bi.Error.Empty"));
            dataset.Name = (dataset.Name ?? string.Empty).Trim();
            if (dataset.Name.Length == 0) throw new InvalidOperationException(Localization.T("Bi.Error.DatasetName"));
            if (existingNames != null && !existingNames.Add(dataset.Name)) throw new InvalidOperationException(Localization.Format("Bi.Error.DuplicateDataset", dataset.Name));
            if (string.IsNullOrWhiteSpace(dataset.Query)) throw new InvalidOperationException(Localization.Format("Bi.Error.DatasetQuery", dataset.Name));
            if (dataset.CalculatedFields == null) dataset.CalculatedFields = new List<BiCalculatedField>();
            if (dataset.CalculatedFields.Count > MaximumCalculatedFields) throw new InvalidOperationException(Localization.Format("Bi.Error.TooManyFields", MaximumCalculatedFields));
            HashSet<string> fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BiCalculatedField field in dataset.CalculatedFields)
            {
                field.Name = (field.Name ?? string.Empty).Trim();
                if (field.Name.Length == 0 || field.Name.IndexOf('[') >= 0 || field.Name.IndexOf(']') >= 0)
                {
                    throw new InvalidOperationException(Localization.Format("Bi.Error.FieldName", field.Name));
                }
                if (!fieldNames.Add(field.Name)) throw new InvalidOperationException(Localization.Format("Bi.Error.DuplicateField", field.Name));
                try
                {
                    BiExpression.Parse(field.Expression);
                }
                catch (FormatException exception)
                {
                    throw new InvalidOperationException(Localization.Format("Bi.Error.FieldExpression", field.Name, exception.Message));
                }
            }
        }

        public static void ValidateWidget(BiWidget widget, ICollection<string> datasetNames)
        {
            if (widget == null) throw new InvalidOperationException(Localization.T("Bi.Error.Empty"));
            widget.Title = (widget.Title ?? string.Empty).Trim();
            widget.Dataset = (widget.Dataset ?? string.Empty).Trim();
            widget.Category = string.IsNullOrWhiteSpace(widget.Category) ? null : widget.Category.Trim();
            widget.Value = string.IsNullOrWhiteSpace(widget.Value) ? null : widget.Value.Trim();
            widget.Filter = string.IsNullOrWhiteSpace(widget.Filter) ? null : widget.Filter.Trim();
            if (datasetNames != null && !datasetNames.Contains(widget.Dataset, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Localization.Format("Bi.Error.UnknownDataset", widget.Title, widget.Dataset));
            }
            if (!Enum.IsDefined(typeof(BiChartKind), widget.Kind) || !Enum.IsDefined(typeof(BiAggregate), widget.Aggregate) || !Enum.IsDefined(typeof(BiDateGrain), widget.DateGrain))
            {
                throw new InvalidOperationException(Localization.Format("Bi.Error.WidgetShape", widget.Title));
            }
            bool needsCategory = widget.Kind == BiChartKind.Bar || widget.Kind == BiChartKind.Line || widget.Kind == BiChartKind.Pie;
            if (needsCategory && widget.Category == null) throw new InvalidOperationException(Localization.Format("Bi.Error.CategoryRequired", widget.Title));
            if (widget.Aggregate != BiAggregate.Count && widget.Value == null) throw new InvalidOperationException(Localization.Format("Bi.Error.ValueRequired", widget.Title));
            widget.TopN = Math.Max(1, Math.Min(MaximumTopN, widget.TopN <= 0 ? 12 : widget.TopN));
            widget.Span = widget.Span >= 2 ? 2 : 1;
            if (widget.Filter != null)
            {
                try
                {
                    BiExpression.Parse(widget.Filter);
                }
                catch (FormatException exception)
                {
                    throw new InvalidOperationException(Localization.Format("Bi.Error.WidgetFilter", widget.Title, exception.Message));
                }
            }
        }

        public static string Serialize(BiDashboard dashboard)
        {
            Validate(dashboard);
            return JsonConvert.SerializeObject(dashboard, Formatting.Indented);
        }

        public static BiDashboard Deserialize(string json)
        {
            BiDashboard dashboard;
            try
            {
                dashboard = JsonConvert.DeserializeObject<BiDashboard>(json ?? string.Empty, new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    TypeNameHandling = TypeNameHandling.None,
                    MaxDepth = 32
                });
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(Localization.Format("Bi.Error.File", exception.Message));
            }
            Validate(dashboard);
            return dashboard;
        }

        public static void Save(string path, BiDashboard dashboard)
        {
            string json = Serialize(dashboard);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public static BiDashboard Load(string path)
        {
            if (new FileInfo(path).Length > 4L * 1024 * 1024) throw new InvalidOperationException(Localization.T("Bi.Error.FileTooLarge"));
            return Deserialize(File.ReadAllText(path, Encoding.UTF8));
        }

        // ------------------------------------------------------------ Loading

        public static bool SupportsProvider(string providerName)
        {
            string provider = (providerName ?? string.Empty).ToLowerInvariant();
            return provider.Length > 0 && !provider.Contains("redis");
        }

        public static bool IsJsonQueryProvider(IDatabase database)
        {
            return database is my_mongodb;
        }

        /// <summary>執行資料集查詢。SQL 必須是單一唯讀敘述；MongoDB 走唯讀 JSON 查詢（find 或不含 $out／$merge 的聚合管線）。</summary>
        public static DataTable Query(IDatabase database, string databaseName, BiDataset dataset)
        {
            if (database == null) throw new InvalidOperationException(Localization.T("Bi.Error.NoConnection"));
            if (!SupportsProvider(database.ProviderName)) throw new InvalidOperationException(Localization.Format("Bi.Error.Provider", database.ProviderName));
            string query = (dataset.Query ?? string.Empty).Trim();
            DataTable table;
            my_mongodb mongo = database as my_mongodb;
            if (mongo != null)
            {
                table = mongo.SelectJsonQuery(databaseName, query);
            }
            else
            {
                string reason;
                if (!ScheduledJobValidator.IsReadOnlySql(query, out reason))
                {
                    throw new InvalidOperationException(Localization.Format("Bi.Error.NotReadOnly", dataset.Name, reason));
                }
                if (database is my_mysql && !string.IsNullOrWhiteSpace(databaseName))
                {
                    query = "USE `" + databaseName.Replace("`", "``") + "`;\r\n" + query.TrimEnd(';', ' ', '\r', '\n', '\t');
                }
                table = database.SelectSQL(query);
            }
            DataSyncService.ThrowIfQueryFailed(table);
            return table;
        }

        /// <summary>正規化查詢結果並依序計算衍生欄位（後面的欄位可參照前面的）。</summary>
        public static BiDatasetData Prepare(DataTable table, BiDataset dataset)
        {
            if (table == null) throw new InvalidOperationException(Localization.T("Bi.Error.Empty"));
            List<BiCalculatedField> fields = dataset == null || dataset.CalculatedFields == null ? new List<BiCalculatedField>() : dataset.CalculatedFields;
            List<string> columns = table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToList();
            foreach (BiCalculatedField field in fields)
            {
                if (columns.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(Localization.Format("Bi.Error.FieldCollides", field.Name));
                }
                columns.Add(field.Name);
            }
            List<BiExpression> expressions = fields.Select(field => BiExpression.Parse(field.Expression)).ToList();
            int sourceCount = table.Columns.Count;
            for (int f = 0; f < expressions.Count; f++)
            {
                foreach (string name in expressions[f].Fields)
                {
                    int index = columns.FindIndex(column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase));
                    if (index < 0 || index >= sourceCount + f)
                    {
                        throw new InvalidOperationException(Localization.Format("Bi.Error.UnknownField", fields[f].Name, name));
                    }
                }
            }

            bool truncated = table.Rows.Count > MaximumRows;
            int rowCount = Math.Min(table.Rows.Count, MaximumRows);
            List<object[]> rows = new List<object[]>(rowCount);
            for (int r = 0; r < rowCount; r++)
            {
                DataRow source = table.Rows[r];
                object[] values = new object[columns.Count];
                for (int c = 0; c < sourceCount; c++) values[c] = BiExpression.Normalize(source[c]);
                for (int f = 0; f < expressions.Count; f++)
                {
                    try
                    {
                        values[sourceCount + f] = expressions[f].Evaluate(name => values[IndexOf(columns, name)]);
                    }
                    catch (FormatException exception)
                    {
                        throw new InvalidOperationException(Localization.Format("Bi.Error.FieldRow", fields[f].Name, r + 1, exception.Message));
                    }
                }
                rows.Add(values);
            }
            return new BiDatasetData(columns, rows, truncated);
        }

        private static int IndexOf(List<string> columns, string name)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            throw new FormatException(Localization.Format("Bi.Error.MissingColumn", name));
        }

        // ------------------------------------------------------------ Computing

        public static string KeyOf(object value, BiDateGrain grain)
        {
            if (value == null) return NullKey;
            if (grain != BiDateGrain.None)
            {
                DateTime date;
                if (TryDate(value, out date))
                {
                    switch (grain)
                    {
                        case BiDateGrain.Year: return date.ToString("yyyy", CultureInfo.InvariantCulture);
                        case BiDateGrain.Month: return date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                        default: return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }
                }
            }
            return BiExpression.ToText(value);
        }

        public static string LabelOf(string key)
        {
            return key == NullKey ? Localization.T("Bi.NullLabel") : key;
        }

        private static bool TryDate(object value, out DateTime date)
        {
            if (value is DateTime)
            {
                date = (DateTime)value;
                return true;
            }
            string text = value as string;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date) && text != null && text.Length >= 8;
        }

        /// <summary>篩選是否會套用到此圖表：圖表參與跨篩選、資料集有該欄位，且不是篩選的來源圖表。</summary>
        public static bool FilterApplies(BiFilter filter, int widgetIndex, BiWidget widget, BiDatasetData data)
        {
            return widget.CrossFilter && filter.SourceWidget != widgetIndex && data.IndexOf(filter.Field) >= 0;
        }

        public static BiWidgetResult Compute(BiWidget widget, int widgetIndex, BiDatasetData data, IList<BiFilter> filters)
        {
            BiWidgetResult result = new BiWidgetResult();
            try
            {
                ComputeCore(widget, widgetIndex, data, filters ?? new List<BiFilter>(), result);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is FormatException || exception is OverflowException)
            {
                result.Points.Clear();
                result.Table = null;
                result.Error = exception.Message;
            }
            return result;
        }

        private static void ComputeCore(BiWidget widget, int widgetIndex, BiDatasetData data, IList<BiFilter> filters, BiWidgetResult result)
        {
            int categoryIndex = widget.Category == null ? -1 : data.IndexOf(widget.Category);
            if (widget.Category != null && categoryIndex < 0) throw new InvalidOperationException(Localization.Format("Bi.Error.MissingColumn", widget.Category));
            int valueIndex = widget.Value == null ? -1 : data.IndexOf(widget.Value);
            if (widget.Value != null && valueIndex < 0) throw new InvalidOperationException(Localization.Format("Bi.Error.MissingColumn", widget.Value));
            BiExpression filterExpression = widget.Filter == null ? null : BiExpression.Parse(widget.Filter);

            List<KeyValuePair<int, BiFilter>> active = filters
                .Where(filter => FilterApplies(filter, widgetIndex, widget, data))
                .Select(filter => new KeyValuePair<int, BiFilter>(data.IndexOf(filter.Field), filter))
                .ToList();

            List<object[]> matched = new List<object[]>();
            for (int r = 0; r < data.Rows.Count; r++)
            {
                object[] row = data.Rows[r];
                bool keep = true;
                foreach (KeyValuePair<int, BiFilter> pair in active)
                {
                    if (!string.Equals(KeyOf(row[pair.Key], pair.Value.Grain), pair.Value.Key, StringComparison.Ordinal))
                    {
                        keep = false;
                        break;
                    }
                }
                if (keep && filterExpression != null)
                {
                    object value;
                    try
                    {
                        value = filterExpression.Evaluate(name => row[IndexOf(data.Columns, name)]);
                    }
                    catch (FormatException exception)
                    {
                        throw new InvalidOperationException(Localization.Format("Bi.Error.FilterRow", r + 1, exception.Message));
                    }
                    keep = value is bool ? (bool)value : value is decimal && (decimal)value != 0m;
                }
                if (keep) matched.Add(row);
            }
            result.MatchedRows = matched.Count;
            result.Total = Aggregate(matched, valueIndex, widget.Aggregate);

            if (categoryIndex < 0)
            {
                if (widget.Kind == BiChartKind.Table) result.Table = RawTable(data, matched);
                return;
            }

            Dictionary<string, List<object[]>> groups = new Dictionary<string, List<object[]>>(StringComparer.Ordinal);
            List<string> order = new List<string>();
            foreach (object[] row in matched)
            {
                string key = KeyOf(row[categoryIndex], widget.DateGrain);
                List<object[]> bucket;
                if (!groups.TryGetValue(key, out bucket))
                {
                    bucket = new List<object[]>();
                    groups.Add(key, bucket);
                    order.Add(key);
                }
                bucket.Add(row);
            }
            result.GroupCount = order.Count;

            List<BiPoint> points = order.Select(key => new BiPoint
            {
                Key = key,
                Label = LabelOf(key),
                Value = Aggregate(groups[key], valueIndex, widget.Aggregate)
            }).ToList();
            if (widget.SortByValue)
            {
                points = points.OrderByDescending(point => point.Value ?? decimal.MinValue).ThenBy(point => point.Label, StringComparer.CurrentCulture).ToList();
            }
            else
            {
                points = points.OrderBy(point => point.Key == NullKey ? 1 : 0).ThenBy(point => point.Key, new CategoryComparer()).ToList();
            }
            int limit = widget.Kind == BiChartKind.Table ? Math.Max(widget.TopN, MaximumTableRows) : widget.TopN;
            result.Points.AddRange(points.Take(limit));

            if (widget.Kind == BiChartKind.Table)
            {
                DataTable table = new DataTable();
                table.Columns.Add(widget.Category + (widget.DateGrain == BiDateGrain.None ? string.Empty : " (" + widget.DateGrain + ")"), typeof(string));
                table.Columns.Add(AggregateCaption(widget), typeof(decimal));
                foreach (BiPoint point in result.Points)
                {
                    table.Rows.Add(point.Label, point.Value.HasValue ? (object)point.Value.Value : DBNull.Value);
                }
                result.Table = table;
            }
        }

        public static string AggregateCaption(BiWidget widget)
        {
            string aggregate = Localization.T("Bi.Aggregate." + widget.Aggregate);
            return widget.Value == null ? aggregate : aggregate + "(" + widget.Value + ")";
        }

        private static DataTable RawTable(BiDatasetData data, List<object[]> rows)
        {
            DataTable table = new DataTable();
            foreach (string column in data.Columns)
            {
                string name = column;
                int suffix = 2;
                while (table.Columns.Contains(name)) name = column + "_" + suffix++;
                table.Columns.Add(name, typeof(string));
            }
            foreach (object[] row in rows.Take(MaximumTableRows))
            {
                table.Rows.Add(row.Select(value => value == null ? (object)DBNull.Value : BiExpression.ToText(value)).ToArray());
            }
            return table;
        }

        public static decimal? Aggregate(List<object[]> rows, int valueIndex, BiAggregate aggregate)
        {
            if (aggregate == BiAggregate.Count)
            {
                return valueIndex < 0 ? rows.Count : rows.Count(row => row[valueIndex] != null);
            }
            if (aggregate == BiAggregate.DistinctCount)
            {
                return rows.Where(row => row[valueIndex] != null).Select(row => KeyOf(row[valueIndex], BiDateGrain.None)).Distinct(StringComparer.Ordinal).Count();
            }

            List<decimal> numbers = new List<decimal>();
            foreach (object[] row in rows)
            {
                object value = row[valueIndex];
                if (value == null) continue;
                if (value is decimal) numbers.Add((decimal)value);
                else if (value is bool) numbers.Add((bool)value ? 1m : 0m);
                else
                {
                    decimal parsed;
                    if (!decimal.TryParse(BiExpression.ToText(value), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        throw new InvalidOperationException(Localization.Format("Bi.Error.NotNumeric", BiExpression.ToText(value)));
                    }
                    numbers.Add(parsed);
                }
            }
            if (numbers.Count == 0) return null;
            switch (aggregate)
            {
                case BiAggregate.Sum: return numbers.Sum();
                case BiAggregate.Average: return numbers.Sum() / numbers.Count;
                case BiAggregate.Minimum: return numbers.Min();
                default: return numbers.Max();
            }
        }

        /// <summary>點選分類：同一圖表再點同一分類取消，點其他分類取代該圖表原本的篩選。</summary>
        public static List<BiFilter> Toggle(IList<BiFilter> filters, int widgetIndex, BiWidget widget, string key)
        {
            List<BiFilter> next = (filters ?? new List<BiFilter>()).Where(filter => filter.SourceWidget != widgetIndex).ToList();
            BiFilter existing = (filters ?? new List<BiFilter>()).FirstOrDefault(filter => filter.SourceWidget == widgetIndex);
            if (existing != null && existing.Key == key) return next;
            if (widget.Category == null || !widget.CrossFilter) return next;
            next.Add(new BiFilter(widgetIndex, widget.Category, widget.DateGrain, key, LabelOf(key)));
            return next;
        }

        /// <summary>刪除圖表後重新編號篩選的來源圖表。</summary>
        public static List<BiFilter> RemoveWidget(IList<BiFilter> filters, int removedIndex)
        {
            return (filters ?? new List<BiFilter>())
                .Where(filter => filter.SourceWidget != removedIndex)
                .Select(filter => filter.SourceWidget > removedIndex
                    ? new BiFilter(filter.SourceWidget - 1, filter.Field, filter.Grain, filter.Key, filter.Label)
                    : filter)
                .ToList();
        }

        public static string FormatNumber(decimal? value)
        {
            if (!value.HasValue) return "—";
            decimal number = value.Value;
            decimal absolute = Math.Abs(number);
            if (absolute >= 1000000000m) return (number / 1000000000m).ToString("0.##", CultureInfo.CurrentCulture) + "B";
            if (absolute >= 1000000m) return (number / 1000000m).ToString("0.##", CultureInfo.CurrentCulture) + "M";
            if (absolute >= 10000m) return number.ToString("N0", CultureInfo.CurrentCulture);
            return number.ToString("#,0.##", CultureInfo.CurrentCulture);
        }

        private sealed class CategoryComparer : IComparer<string>
        {
            public int Compare(string x, string y)
            {
                decimal a, b;
                if (decimal.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                    decimal.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                {
                    return a.CompareTo(b);
                }
                return string.Compare(x, y, StringComparison.Ordinal);
            }
        }
    }
}
