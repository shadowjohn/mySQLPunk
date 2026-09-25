using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib
{
    public sealed class DataFormatCount
    {
        public string Format { get; set; }
        public long Count { get; set; }
    }

    public sealed class DataFormatAnomaly
    {
        public string Value { get; set; }
        public long Count { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>文字欄位的格式分析結果（以抽樣的相異值與出現次數計算）。</summary>
    public sealed class DataFormatReport
    {
        public DataFormatReport()
        {
            Formats = new List<DataFormatCount>();
            Anomalies = new List<DataFormatAnomaly>();
            Issues = new List<string>();
        }

        public long ValueCount { get; set; }
        /// <summary>佔多數的語意格式（例如 Email、Date yyyy-MM-dd）；沒有明顯格式時為 null。</summary>
        public string DominantFormat { get; set; }
        public double Coverage { get; set; }
        public List<DataFormatCount> Formats { get; private set; }
        public List<DataFormatAnomaly> Anomalies { get; private set; }
        public List<string> Issues { get; private set; }
        public long AnomalyCount { get { return Anomalies.Sum(item => item.Count); } }

        public string Summary
        {
            get
            {
                if (DominantFormat == null) return Issues.Count == 0 ? string.Empty : Localization.Format("DataFormat.IssuesOnly", Issues.Count);
                string text = Localization.Format("DataFormat.Summary", DominantFormat, Coverage.ToString("P0", CultureInfo.CurrentCulture));
                if (AnomalyCount > 0) text += " · " + Localization.Format("DataFormat.AnomalyCount", AnomalyCount);
                return text;
            }
        }
    }

    /// <summary>
    /// 格式異常偵測：把每個值歸類為語意格式（Email、URL、電話、日期、UUID、IPv4、整數／小數文字、布林文字）或字元樣式
    /// （字母→A／a、數字→9），找出佔多數的格式並列出不符的值；另外標示前後空白、只差大小寫的重複值與「N/A」等占位值。
    /// 只做字串分析，不存取資料庫。
    /// </summary>
    public static class DataFormatAnalyzer
    {
        public const double DominantThreshold = 0.8;
        public const int MaximumAnomalies = 20;

        private static readonly KeyValuePair<string, Regex>[] SemanticFormats =
        {
            Format("Email", @"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$"),
            Format("URL", @"^https?://[^\s/$.?#][^\s]*$"),
            Format("UUID", @"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$"),
            Format("IPv4", @"^((25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(25[0-5]|2[0-4]\d|1?\d?\d)$"),
            Format("Date yyyy-MM-dd", @"^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$"),
            Format("DateTime yyyy-MM-dd HH:mm:ss", @"^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])[ T]([01]\d|2[0-3]):[0-5]\d(:[0-5]\d(\.\d+)?)?(Z|[+\-]\d{2}:?\d{2})?$"),
            Format("Date yyyy/MM/dd", @"^\d{4}/(0?[1-9]|1[0-2])/(0?[1-9]|[12]\d|3[01])$"),
            Format("Date dd/MM/yyyy or MM/dd/yyyy", @"^(0?[1-9]|[12]\d|3[01])/(0?[1-9]|[12]\d|3[01])/\d{4}$"),
            Format("Integer", @"^[+\-]?\d{1,18}$"),
            Format("Decimal", @"^[+\-]?\d+[.,]\d+$"),
            Format("Phone", @"^\+?[0-9][0-9 \-().]{6,18}[0-9]$"),
            Format("Boolean", @"^(true|false|yes|no|y|n|t|f)$")
        };

        private static readonly HashSet<string> Placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "n/a", "na", "null", "none", "nil", "-", "--", "?", "unknown", "undefined", "tbd", "0000-00-00", "無", "未知", "空"
        };

        private static KeyValuePair<string, Regex> Format(string name, string pattern)
        {
            return new KeyValuePair<string, Regex>(name, new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));
        }

        /// <summary>傳入相異值與出現次數（通常是 GROUP BY 的結果）。</summary>
        public static DataFormatReport Analyze(IEnumerable<KeyValuePair<string, long>> values)
        {
            DataFormatReport report = new DataFormatReport();
            List<KeyValuePair<string, long>> items = (values ?? Enumerable.Empty<KeyValuePair<string, long>>())
                .Where(item => item.Key != null && item.Value > 0).ToList();
            report.ValueCount = items.Sum(item => item.Value);
            if (report.ValueCount == 0) return report;

            Dictionary<string, long> formats = new Dictionary<string, long>(StringComparer.Ordinal);
            List<KeyValuePair<KeyValuePair<string, long>, string>> classified = new List<KeyValuePair<KeyValuePair<string, long>, string>>();
            long whitespace = 0, empty = 0, placeholder = 0;
            foreach (KeyValuePair<string, long> item in items)
            {
                string value = item.Key;
                if (value.Length == 0)
                {
                    empty += item.Value;
                    continue;
                }
                if (value != value.Trim()) whitespace += item.Value;
                if (Placeholders.Contains(value.Trim()))
                {
                    placeholder += item.Value;
                    classified.Add(new KeyValuePair<KeyValuePair<string, long>, string>(item, null));
                    continue;
                }
                string format = Classify(value.Trim());
                long count;
                formats.TryGetValue(format, out count);
                formats[format] = count + item.Value;
                classified.Add(new KeyValuePair<KeyValuePair<string, long>, string>(item, format));
            }

            report.Formats.AddRange(formats.OrderByDescending(pair => pair.Value).Select(pair => new DataFormatCount { Format = pair.Key, Count = pair.Value }));
            long meaningful = report.ValueCount - empty;
            DataFormatCount top = report.Formats.FirstOrDefault();
            if (top != null && meaningful > 0 && (double)top.Count / meaningful >= DominantThreshold && top.Count >= 3)
            {
                report.DominantFormat = top.Format;
                report.Coverage = (double)top.Count / meaningful;
                foreach (KeyValuePair<KeyValuePair<string, long>, string> entry in classified
                             .Where(entry => entry.Value != report.DominantFormat)
                             .OrderByDescending(entry => entry.Key.Value)
                             .Take(MaximumAnomalies))
                {
                    report.Anomalies.Add(new DataFormatAnomaly
                    {
                        Value = entry.Key.Key,
                        Count = entry.Key.Value,
                        Reason = entry.Value == null
                            ? Localization.T("DataFormat.Reason.Placeholder")
                            : Localization.Format("DataFormat.Reason.Format", entry.Value)
                    });
                }
            }

            if (empty > 0) report.Issues.Add(Localization.Format("DataFormat.Issue.Empty", empty));
            if (whitespace > 0) report.Issues.Add(Localization.Format("DataFormat.Issue.Whitespace", whitespace));
            if (placeholder > 0) report.Issues.Add(Localization.Format("DataFormat.Issue.Placeholder", placeholder));
            List<string> caseVariants = items
                .Select(item => item.Key.Trim())
                .Where(value => value.Length > 0 && value.Any(char.IsLetter))
                .Distinct(StringComparer.Ordinal)
                .GroupBy(value => value.ToUpperInvariant(), StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => string.Join(" / ", group.Take(3)))
                .Take(5)
                .ToList();
            if (caseVariants.Count > 0) report.Issues.Add(Localization.Format("DataFormat.Issue.Case", string.Join("；", caseVariants)));
            return report;
        }

        /// <summary>語意格式優先；都不符合時回傳字元樣式，例如「AAA-9999」。</summary>
        public static string Classify(string value)
        {
            foreach (KeyValuePair<string, Regex> format in SemanticFormats)
            {
                if (format.Value.IsMatch(value)) return format.Key;
            }
            return Shape(value);
        }

        /// <summary>字元樣式：大寫→A、小寫→a、數字→9、空白→_、其他字元保留；連續 4 個以上同類字元以「A+」表示，最長 24 字。</summary>
        public static string Shape(string value)
        {
            StringBuilder shape = new StringBuilder();
            char previous = '\0';
            int run = 0;
            foreach (char c in value)
            {
                char mapped = char.IsUpper(c) ? 'A' : char.IsLower(c) ? 'a' : char.IsDigit(c) ? '9' : char.IsWhiteSpace(c) ? '_' : char.IsLetter(c) ? 'L' : c;
                if (mapped == previous)
                {
                    run++;
                    if (run == 4)
                    {
                        shape.Length -= 2;
                        shape.Append('+');
                    }
                    if (run >= 4) continue;
                }
                else
                {
                    previous = mapped;
                    run = 1;
                }
                shape.Append(mapped);
                if (shape.Length >= 24) return shape.ToString(0, 24) + "…";
            }
            return shape.ToString();
        }
    }
}
