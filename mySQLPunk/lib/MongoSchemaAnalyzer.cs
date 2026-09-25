using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MongoDB.Bson;

namespace mySQLPunk.lib
{
    public sealed class MongoFieldStats
    {
        public MongoFieldStats()
        {
            TypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            TopValues = new List<KeyValuePair<string, int>>();
            OutlierSamples = new List<string>();
            Anomalies = new List<string>();
        }

        /// <summary>點號分隔的路徑；陣列元素以 [] 表示，例如 items[].sku。</summary>
        public string Path { get; set; }
        public int Depth { get; set; }
        public bool InsideArray { get; set; }
        /// <summary>至少出現一次此路徑的文件數。</summary>
        public int DocumentCount { get; set; }
        /// <summary>出現次數（陣列元素路徑可能大於文件數）。</summary>
        public int Occurrences { get; set; }
        public int NullCount { get; set; }
        public Dictionary<string, int> TypeCounts { get; private set; }
        public int NumericCount { get; set; }
        public double? NumericMin { get; set; }
        public double? NumericMax { get; set; }
        public double? NumericMean { get; set; }
        public double? LowerFence { get; set; }
        public double? UpperFence { get; set; }
        public int OutlierCount { get; set; }
        public List<string> OutlierSamples { get; private set; }
        public int? MinLength { get; set; }
        public int? MaxLength { get; set; }
        public int EmptyStringCount { get; set; }
        public DateTime? DateMin { get; set; }
        public DateTime? DateMax { get; set; }
        public int? ArrayMinLength { get; set; }
        public int? ArrayMaxLength { get; set; }
        public int DistinctValues { get; set; }
        public bool DistinctCapped { get; set; }
        public List<KeyValuePair<string, int>> TopValues { get; private set; }
        public List<string> Anomalies { get; private set; }

        public string TypeSummary
        {
            get
            {
                int total = Math.Max(1, TypeCounts.Values.Sum());
                return string.Join(", ", TypeCounts.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => item.Key + " " + (item.Value * 100.0 / total).ToString("0.#", CultureInfo.InvariantCulture) + "%"));
            }
        }
    }

    public sealed class MongoSchemaReport
    {
        public MongoSchemaReport()
        {
            Fields = new List<MongoFieldStats>();
            Warnings = new List<string>();
        }

        public int DocumentCount { get; set; }
        public List<MongoFieldStats> Fields { get; private set; }
        public List<string> Warnings { get; private set; }

        public double Presence(MongoFieldStats field)
        {
            return DocumentCount == 0 ? 0 : field.DocumentCount * 100.0 / DocumentCount;
        }
    }

    /// <summary>
    /// 抽樣文件的結構描述分析：展開巢狀文件與陣列路徑，統計出現率、型別分佈、NULL、數值範圍與四分位距極端值、
    /// 字串長度、日期範圍、陣列長度與常見值，並標出混合型別、稀疏欄位、只差大小寫的欄位名稱等異常。只讀取傳入的文件。
    /// </summary>
    public static class MongoSchemaAnalyzer
    {
        public const int MaximumPaths = 2000;
        public const int MaximumDepth = 20;
        private const int DistinctLimit = 1000;
        private const int TopValueCount = 5;
        private const int OutlierSampleCount = 5;
        private const double SparseThreshold = 10.0;

        public static MongoSchemaReport AnalyzeJson(IEnumerable<string> documents)
        {
            return Analyze(documents.Select(json => BsonDocument.Parse(json)).ToList());
        }

        public static MongoSchemaReport Analyze(IList<BsonDocument> documents)
        {
            MongoSchemaReport report = new MongoSchemaReport { DocumentCount = documents == null ? 0 : documents.Count };
            if (documents == null) return report;
            Dictionary<string, Accumulator> paths = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
            bool pathLimitHit = false;
            bool depthLimitHit = false;
            foreach (BsonDocument document in documents)
            {
                string id = document.Contains("_id") ? Describe(document["_id"]) : string.Empty;
                HashSet<string> seenInDocument = new HashSet<string>(StringComparer.Ordinal);
                Walk(document, string.Empty, 1, false, id, paths, seenInDocument, ref pathLimitHit, ref depthLimitHit);
                foreach (string path in seenInDocument) paths[path].Stats.DocumentCount++;
            }

            if (pathLimitHit) report.Warnings.Add(Localization.Format("MongoSchema.Warn.PathLimit", MaximumPaths));
            if (depthLimitHit) report.Warnings.Add(Localization.Format("MongoSchema.Warn.DepthLimit", MaximumDepth));

            foreach (Accumulator accumulator in paths.Values)
            {
                accumulator.Finish(report.DocumentCount);
                report.Fields.Add(accumulator.Stats);
            }

            // 同一層的欄位名稱只差大小寫，通常是寫入端不一致造成的。
            foreach (IGrouping<string, MongoFieldStats> group in report.Fields
                         .GroupBy(field => field.Path.ToUpperInvariant(), StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
            {
                string names = string.Join(", ", group.Select(field => field.Path));
                foreach (MongoFieldStats field in group) field.Anomalies.Add(Localization.Format("MongoSchema.Anomaly.CaseVariant", names));
            }

            report.Fields.Sort((left, right) =>
            {
                if (left.Path == "_id") return right.Path == "_id" ? 0 : -1;
                if (right.Path == "_id") return 1;
                return StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
            });
            return report;
        }

        /// <summary>純文字報告，供複製或存檔。</summary>
        public static string BuildTextReport(MongoSchemaReport report, string title)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine(title);
            text.AppendLine(Localization.Format("MongoSchema.Report.Sampled", report.DocumentCount));
            foreach (string warning in report.Warnings) text.AppendLine("! " + warning);
            text.AppendLine();
            foreach (MongoFieldStats field in report.Fields)
            {
                text.AppendLine(field.Path);
                text.AppendLine("  " + Localization.Format("MongoSchema.Report.Presence", report.Presence(field).ToString("0.#", CultureInfo.InvariantCulture), field.DocumentCount, field.Occurrences));
                text.AppendLine("  " + Localization.Format("MongoSchema.Report.Types", field.TypeSummary));
                string range = RangeText(field);
                if (range.Length > 0) text.AppendLine("  " + range);
                if (field.TopValues.Count > 0) text.AppendLine("  " + Localization.Format("MongoSchema.Report.Top", TopText(field)));
                foreach (string anomaly in field.Anomalies) text.AppendLine("  ! " + anomaly);
            }
            return text.ToString();
        }

        public static string RangeText(MongoFieldStats field)
        {
            List<string> parts = new List<string>();
            if (field.NumericMin.HasValue)
            {
                parts.Add(Localization.Format("MongoSchema.Range.Number",
                    Number(field.NumericMin.Value), Number(field.NumericMax.Value), Number(field.NumericMean.Value)));
            }
            if (field.MinLength.HasValue) parts.Add(Localization.Format("MongoSchema.Range.Length", field.MinLength.Value, field.MaxLength.Value));
            if (field.DateMin.HasValue)
            {
                parts.Add(Localization.Format("MongoSchema.Range.Date",
                    field.DateMin.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    field.DateMax.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            }
            if (field.ArrayMinLength.HasValue) parts.Add(Localization.Format("MongoSchema.Range.Array", field.ArrayMinLength.Value, field.ArrayMaxLength.Value));
            return string.Join("; ", parts);
        }

        public static string TopText(MongoFieldStats field)
        {
            return string.Join(", ", field.TopValues.Select(item => item.Key + " (" + item.Value.ToString(CultureInfo.InvariantCulture) + ")")) +
                   (field.DistinctCapped
                       ? " · " + Localization.Format("MongoSchema.DistinctCapped", DistinctLimit)
                       : " · " + Localization.Format("MongoSchema.Distinct", field.DistinctValues));
        }

        private static void Walk(
            BsonDocument document,
            string prefix,
            int depth,
            bool insideArray,
            string id,
            Dictionary<string, Accumulator> paths,
            HashSet<string> seen,
            ref bool pathLimitHit,
            ref bool depthLimitHit)
        {
            foreach (BsonElement element in document)
            {
                string path = prefix.Length == 0 ? element.Name : prefix + "." + element.Name;
                Visit(path, element.Value, depth, insideArray, id, paths, seen, ref pathLimitHit, ref depthLimitHit);
            }
        }

        private static void Visit(
            string path,
            BsonValue value,
            int depth,
            bool insideArray,
            string id,
            Dictionary<string, Accumulator> paths,
            HashSet<string> seen,
            ref bool pathLimitHit,
            ref bool depthLimitHit)
        {
            Accumulator accumulator;
            if (!paths.TryGetValue(path, out accumulator))
            {
                if (paths.Count >= MaximumPaths)
                {
                    pathLimitHit = true;
                    return;
                }
                accumulator = new Accumulator(path, depth, insideArray);
                paths.Add(path, accumulator);
            }

            seen.Add(path);
            accumulator.Add(value, id);
            if (depth >= MaximumDepth)
            {
                if (value.IsBsonDocument || value.IsBsonArray) depthLimitHit = true;
                return;
            }

            if (value.IsBsonDocument)
            {
                Walk(value.AsBsonDocument, path, depth + 1, insideArray, id, paths, seen, ref pathLimitHit, ref depthLimitHit);
            }
            else if (value.IsBsonArray)
            {
                foreach (BsonValue item in value.AsBsonArray)
                {
                    Visit(path + "[]", item, depth + 1, true, id, paths, seen, ref pathLimitHit, ref depthLimitHit);
                }
            }
        }

        private static string Describe(BsonValue value)
        {
            string text = value.IsString ? value.AsString : value.ToString();
            return text.Length > 60 ? text.Substring(0, 60) + "…" : text;
        }

        private static string Number(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private sealed class Accumulator
        {
            private readonly List<KeyValuePair<double, string>> numbers = new List<KeyValuePair<double, string>>();
            private readonly Dictionary<string, int> distinct = new Dictionary<string, int>(StringComparer.Ordinal);
            private int numericStrings;
            private int stringCount;
            private int longStrings;

            public Accumulator(string path, int depth, bool insideArray)
            {
                Stats = new MongoFieldStats { Path = path, Depth = depth, InsideArray = insideArray };
            }

            public MongoFieldStats Stats { get; private set; }

            public void Add(BsonValue value, string id)
            {
                Stats.Occurrences++;
                string type = value.BsonType.ToString();
                int count;
                Stats.TypeCounts.TryGetValue(type, out count);
                Stats.TypeCounts[type] = count + 1;
                switch (value.BsonType)
                {
                    case BsonType.Null:
                    case BsonType.Undefined:
                        Stats.NullCount++;
                        return;
                    case BsonType.Int32:
                    case BsonType.Int64:
                    case BsonType.Double:
                    case BsonType.Decimal128:
                        double number = value.BsonType == BsonType.Decimal128 ? Decimal128.ToDouble(value.AsDecimal128) : value.ToDouble();
                        if (!double.IsNaN(number) && !double.IsInfinity(number)) numbers.Add(new KeyValuePair<double, string>(number, id));
                        Track(Number(number));
                        return;
                    case BsonType.String:
                        string text = value.AsString;
                        stringCount++;
                        Stats.MinLength = Stats.MinLength.HasValue ? Math.Min(Stats.MinLength.Value, text.Length) : text.Length;
                        Stats.MaxLength = Stats.MaxLength.HasValue ? Math.Max(Stats.MaxLength.Value, text.Length) : text.Length;
                        if (text.Length == 0) Stats.EmptyStringCount++;
                        if (text.Length > 1000) longStrings++;
                        double parsed;
                        if (text.Trim().Length > 0 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) numericStrings++;
                        Track(text.Length > 80 ? text.Substring(0, 80) + "…" : text);
                        return;
                    case BsonType.DateTime:
                        DateTime date = value.ToUniversalTime();
                        if (!Stats.DateMin.HasValue || date < Stats.DateMin.Value) Stats.DateMin = date;
                        if (!Stats.DateMax.HasValue || date > Stats.DateMax.Value) Stats.DateMax = date;
                        return;
                    case BsonType.Boolean:
                        Track(value.AsBoolean ? "true" : "false");
                        return;
                    case BsonType.Array:
                        int length = value.AsBsonArray.Count;
                        Stats.ArrayMinLength = Stats.ArrayMinLength.HasValue ? Math.Min(Stats.ArrayMinLength.Value, length) : length;
                        Stats.ArrayMaxLength = Stats.ArrayMaxLength.HasValue ? Math.Max(Stats.ArrayMaxLength.Value, length) : length;
                        return;
                    case BsonType.ObjectId:
                        if (Stats.Path != "_id") Track(value.AsObjectId.ToString());
                        return;
                    default:
                        return;
                }
            }

            private void Track(string key)
            {
                int count;
                if (distinct.TryGetValue(key, out count))
                {
                    distinct[key] = count + 1;
                }
                else if (distinct.Count < DistinctLimit)
                {
                    distinct[key] = 1;
                }
                else
                {
                    Stats.DistinctCapped = true;
                }
            }

            public void Finish(int documentCount)
            {
                Stats.DistinctValues = distinct.Count;
                if (distinct.Count > 0 && Stats.Path != "_id")
                {
                    Stats.TopValues.AddRange(distinct.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal).Take(TopValueCount));
                }

                if (numbers.Count > 0)
                {
                    List<KeyValuePair<double, string>> sorted = numbers.OrderBy(item => item.Key).ToList();
                    Stats.NumericCount = sorted.Count;
                    Stats.NumericMin = sorted[0].Key;
                    Stats.NumericMax = sorted[sorted.Count - 1].Key;
                    Stats.NumericMean = sorted.Average(item => item.Key);
                    // _id 是識別碼，大小沒有分佈意義，不做極端值判斷。
                    if (sorted.Count >= 8 && Stats.Path != "_id")
                    {
                        double q1 = Quantile(sorted, 0.25);
                        double q3 = Quantile(sorted, 0.75);
                        double spread = q3 - q1;
                        Stats.LowerFence = q1 - 1.5 * spread;
                        Stats.UpperFence = q3 + 1.5 * spread;
                        List<KeyValuePair<double, string>> outliers = sorted.Where(item => item.Key < Stats.LowerFence || item.Key > Stats.UpperFence).ToList();
                        Stats.OutlierCount = outliers.Count;
                        // 最極端的在前：先取離中位數最遠的。
                        double median = Quantile(sorted, 0.5);
                        Stats.OutlierSamples.AddRange(outliers.OrderByDescending(item => Math.Abs(item.Key - median))
                            .Take(OutlierSampleCount)
                            .Select(item => Number(item.Key) + (item.Value.Length > 0 ? " (_id " + item.Value + ")" : string.Empty)));
                        if (outliers.Count > 0)
                        {
                            Stats.Anomalies.Add(Localization.Format("MongoSchema.Anomaly.Outliers", outliers.Count, Number(Stats.LowerFence.Value), Number(Stats.UpperFence.Value),
                                string.Join(", ", Stats.OutlierSamples)));
                        }
                    }
                }

                List<string> nonNullTypes = Stats.TypeCounts.Keys.Where(type => type != "Null" && type != "Undefined").ToList();
                bool numericTypes = nonNullTypes.Any(type => type == "Int32" || type == "Int64" || type == "Double" || type == "Decimal128");
                // Int32／Int64／Double 混用在 MongoDB 很常見，不算異常；數字與其他型別混用才算。
                List<string> families = nonNullTypes.Select(type => type == "Int32" || type == "Int64" || type == "Double" || type == "Decimal128" ? "Number" : type).Distinct().ToList();
                if (families.Count > 1) Stats.Anomalies.Add(Localization.Format("MongoSchema.Anomaly.MixedTypes", Stats.TypeSummary));
                if (numericTypes && numericStrings > 0) Stats.Anomalies.Add(Localization.Format("MongoSchema.Anomaly.NumericStrings", numericStrings));
                if (!numericTypes && stringCount >= 5 && numericStrings == stringCount) Stats.Anomalies.Add(Localization.T("MongoSchema.Anomaly.AllNumericStrings"));
                if (documentCount > 0 && !Stats.InsideArray && Stats.Depth == 1 && Stats.DocumentCount * 100.0 / documentCount < SparseThreshold)
                {
                    Stats.Anomalies.Add(Localization.Format("MongoSchema.Anomaly.Sparse", Stats.DocumentCount, documentCount));
                }
                if (Stats.EmptyStringCount > 0) Stats.Anomalies.Add(Localization.Format("MongoSchema.Anomaly.EmptyStrings", Stats.EmptyStringCount));
                if (longStrings > 0) Stats.Anomalies.Add(Localization.Format("MongoSchema.Anomaly.LongStrings", longStrings));
                if (Stats.Occurrences > 0 && Stats.NullCount == Stats.Occurrences) Stats.Anomalies.Add(Localization.T("MongoSchema.Anomaly.AlwaysNull"));
            }

            private static double Quantile(List<KeyValuePair<double, string>> sorted, double quantile)
            {
                double position = (sorted.Count - 1) * quantile;
                int lower = (int)Math.Floor(position);
                int upper = (int)Math.Ceiling(position);
                return sorted[lower].Key + (sorted[upper].Key - sorted[lower].Key) * (position - lower);
            }
        }
    }
}
