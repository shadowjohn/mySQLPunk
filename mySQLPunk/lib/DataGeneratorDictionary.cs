using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib
{
    /// <summary>資料產生器的字典：一組值與權重，產生時依權重隨機挑選。</summary>
    public sealed class DataGeneratorDictionary
    {
        private readonly long[] cumulative;

        public DataGeneratorDictionary(string name, IList<string> values, IList<int> weights, bool builtIn)
        {
            if (values == null || values.Count == 0) throw new InvalidOperationException(Localization.Format("DataGen.Dictionary.Error.Empty", name));
            Name = name;
            BuiltIn = builtIn;
            Values = values.ToList();
            Weights = weights == null ? Enumerable.Repeat(1, values.Count).ToList() : weights.ToList();
            cumulative = new long[Values.Count];
            long total = 0;
            for (int i = 0; i < Values.Count; i++)
            {
                total += Weights[i];
                cumulative[i] = total;
            }
            TotalWeight = total;
        }

        public string Name { get; private set; }
        public bool BuiltIn { get; private set; }
        public List<string> Values { get; private set; }
        public List<int> Weights { get; private set; }
        public long TotalWeight { get; private set; }

        public string Pick(Random random)
        {
            long target = (long)(random.NextDouble() * TotalWeight);
            int index = Array.BinarySearch(cumulative, target + 1);
            if (index < 0) index = ~index;
            return Values[Math.Min(index, Values.Count - 1)];
        }
    }

    /// <summary>
    /// 字典存放在本機資料夾，每個字典一個 UTF-8 文字檔：一行一個值，可在值後加 Tab 與權重（1–1,000,000），
    /// # 開頭為註解。內建字典唯讀；使用者字典名稱不可與內建字典相同。
    /// </summary>
    public static class DataGeneratorDictionaryStore
    {
        public const string FileExtension = ".txt";
        public const int MaximumEntries = 100000;
        public const int MaximumValueLength = 4000;
        public const int MaximumWeight = 1000000;
        public const long MaximumFileBytes = 8L * 1024 * 1024;
        private static readonly Regex NamePattern = new Regex(@"^[\p{L}\p{N}_\- ]{1,60}$", RegexOptions.CultureInvariant);

        public static string DefaultDirectory
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
                return Path.Combine(root, "mySQLPunk", "datagen-dictionaries");
            }
        }

        private static readonly Dictionary<string, string> BuiltInSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 常見姓氏依大致比例加權。
            { "zh-TW 姓氏", "陳\t112\n林\t83\n黃\t60\n張\t53\n李\t51\n王\t41\n吳\t40\n劉\t32\n蔡\t29\n楊\t26\n許\t23\n鄭\t19\n謝\t18\n郭\t17\n洪\t16\n曾\t15\n邱\t15\n廖\t14\n賴\t14\n周\t13\n徐\t12\n蘇\t12\n葉\t12\n莊\t11\n呂\t10\n江\t10\n何\t10\n蕭\t9\n羅\t9\n高\t9" },
            { "zh-TW 名字", "家豪\n志明\n俊傑\n建宏\n承恩\n宥廷\n冠宇\n柏翰\n品睿\n彥廷\n怡君\n雅婷\n淑芬\n佳穎\n詩涵\n欣妤\n子涵\n宜蓁\n思妤\n語彤" },
            { "zh-TW 縣市", "臺北市\t25\n新北市\t40\n桃園市\t23\n臺中市\t28\n臺南市\t18\n高雄市\t27\n基隆市\t4\n新竹市\t4\n新竹縣\t6\n苗栗縣\t5\n彰化縣\t12\n南投縣\t5\n雲林縣\t7\n嘉義市\t3\n嘉義縣\t5\n屏東縣\t8\n宜蘭縣\t4\n花蓮縣\t3\n臺東縣\t2\n澎湖縣\t1\n金門縣\t1\n連江縣\t1" },
            { "en first names", "James\nMary\nJohn\nPatricia\nRobert\nJennifer\nMichael\nLinda\nWilliam\nElizabeth\nDavid\nBarbara\nRichard\nSusan\nJoseph\nJessica\nThomas\nSarah\nCharles\nKaren\nDaniel\nNancy\nMatthew\nLisa\nAnthony\nBetty\nMark\nSandra\nSteven\nAshley" },
            { "en last names", "Smith\nJohnson\nWilliams\nBrown\nJones\nGarcia\nMiller\nDavis\nRodriguez\nMartinez\nHernandez\nLopez\nGonzalez\nWilson\nAnderson\nThomas\nTaylor\nMoore\nJackson\nMartin\nLee\nPerez\nThompson\nWhite\nHarris" },
            { "order status", "pending\t10\npaid\t50\nshipped\t25\ndelivered\t60\ncancelled\t5\nrefunded\t2" }
        };

        public static IList<string> BuiltInNames { get { return BuiltInSources.Keys.ToList(); } }

        public static bool IsValidName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name == name.Trim() && NamePattern.IsMatch(name);
        }

        /// <summary>解析字典文字；錯誤會指出行號。</summary>
        public static DataGeneratorDictionary Parse(string name, string text, bool builtIn = false)
        {
            List<string> values = new List<string>();
            List<int> weights = new List<int>();
            string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                string value = line;
                int weight = 1;
                int tab = line.LastIndexOf('\t');
                if (tab >= 0)
                {
                    string weightText = line.Substring(tab + 1).Trim();
                    if (!int.TryParse(weightText, NumberStyles.None, CultureInfo.InvariantCulture, out weight) || weight < 1 || weight > MaximumWeight)
                    {
                        throw new InvalidOperationException(Localization.Format("DataGen.Dictionary.Error.Weight", name, index + 1, MaximumWeight));
                    }
                    value = line.Substring(0, tab);
                }
                value = value.Trim();
                if (value.Length == 0) continue;
                if (value.Length > MaximumValueLength) throw new InvalidOperationException(Localization.Format("DataGen.Dictionary.Error.ValueLength", name, index + 1, MaximumValueLength));
                if (value.Any(c => char.IsControl(c))) throw new InvalidOperationException(Localization.Format("DataGen.Dictionary.Error.Control", name, index + 1));
                values.Add(value);
                weights.Add(weight);
                if (values.Count > MaximumEntries) throw new InvalidOperationException(Localization.Format("DataGen.Dictionary.Error.TooMany", name, MaximumEntries));
            }
            return new DataGeneratorDictionary(name, values, weights, builtIn);
        }

        public static string Serialize(DataGeneratorDictionary dictionary)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < dictionary.Values.Count; i++)
            {
                builder.Append(dictionary.Values[i]);
                if (dictionary.Weights[i] != 1) builder.Append('\t').Append(dictionary.Weights[i].ToString(CultureInfo.InvariantCulture));
                builder.Append('\n');
            }
            return builder.ToString();
        }

        /// <summary>內建字典加上資料夾中的使用者字典（依名稱排序）。</summary>
        public static List<string> List(string directory)
        {
            List<string> names = BuiltInNames.ToList();
            if (Directory.Exists(directory))
            {
                names.AddRange(Directory.GetFiles(directory, "*" + FileExtension)
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => IsValidName(name) && !BuiltInSources.ContainsKey(name))
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase));
            }
            return names;
        }

        public static bool IsBuiltIn(string name)
        {
            return name != null && BuiltInSources.ContainsKey(name);
        }

        /// <summary>載入字典；找不到時回傳 null。</summary>
        public static DataGeneratorDictionary Load(string directory, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string source;
            if (BuiltInSources.TryGetValue(name.Trim(), out source)) return Parse(name.Trim(), source, true);
            if (!IsValidName(name.Trim())) return null;
            string path = PathFor(directory, name.Trim());
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidOperationException(Localization.Format("DataGen.Dictionary.Error.TooLarge", name, MaximumFileBytes / (1024 * 1024)));
            return Parse(name.Trim(), File.ReadAllText(path, Encoding.UTF8));
        }

        public static string BuiltInText(string name)
        {
            string source;
            return BuiltInSources.TryGetValue(name ?? string.Empty, out source) ? source : null;
        }

        /// <summary>驗證並儲存使用者字典（原子取代）；回傳解析後的字典。</summary>
        public static DataGeneratorDictionary Save(string directory, string name, string text)
        {
            name = (name ?? string.Empty).Trim();
            if (!IsValidName(name)) throw new InvalidOperationException(Localization.T("DataGen.Dictionary.Error.Name"));
            if (IsBuiltIn(name)) throw new InvalidOperationException(Localization.Format("DataGen.Dictionary.Error.BuiltIn", name));
            DataGeneratorDictionary dictionary = Parse(name, text);
            Directory.CreateDirectory(directory);
            string path = PathFor(directory, name);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, Serialize(dictionary), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            return dictionary;
        }

        public static void Delete(string directory, string name)
        {
            if (IsBuiltIn(name)) throw new InvalidOperationException(Localization.Format("DataGen.Dictionary.Error.BuiltIn", name));
            if (!IsValidName(name)) return;
            string path = PathFor(directory, name);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>從 CSV／文字檔匯入：取第一欄（可選擇跳過標題列），第二欄若是整數則當權重。</summary>
        public static string ImportText(string content, bool hasHeader)
        {
            StringBuilder builder = new StringBuilder();
            bool first = true;
            foreach (string raw in (content ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (first && hasHeader)
                {
                    first = false;
                    continue;
                }
                first = false;
                if (raw.Trim().Length == 0) continue;
                List<string> fields = SplitCsv(raw);
                string value = fields.Count > 0 ? fields[0].Trim() : string.Empty;
                if (value.Length == 0) continue;
                int weight;
                builder.Append(value.Replace('\t', ' '));
                if (fields.Count > 1 && int.TryParse(fields[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out weight)) builder.Append('\t').Append(weight.ToString(CultureInfo.InvariantCulture));
                builder.Append('\n');
            }
            return builder.ToString();
        }

        private static List<string> SplitCsv(string line)
        {
            List<string> fields = new List<string>();
            StringBuilder current = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else if (c == '"') quoted = false;
                    else current.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',' || c == '\t' || c == ';')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else current.Append(c);
            }
            fields.Add(current.ToString());
            return fields;
        }

        private static string PathFor(string directory, string name)
        {
            return Path.Combine(directory, name + FileExtension);
        }
    }
}
