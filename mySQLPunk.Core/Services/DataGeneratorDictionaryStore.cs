using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MySqlPunk.Core.Services;

/// <summary>資料產生器的字典：一組值與權重，產生時依權重隨機挑選。</summary>
public sealed class DataGeneratorDictionary
{
    private readonly long[] _cumulative;

    public DataGeneratorDictionary(string name, IReadOnlyList<string> values, IReadOnlyList<int> weights, bool builtIn)
    {
        if (values.Count == 0)
        {
            throw new InvalidOperationException($"字典「{name}」沒有任何值。");
        }

        Name = name;
        BuiltIn = builtIn;
        Values = values.ToList();
        Weights = weights.ToList();
        _cumulative = new long[Values.Count];
        long total = 0;
        for (var i = 0; i < Values.Count; i++)
        {
            total += Weights[i];
            _cumulative[i] = total;
        }

        TotalWeight = total;
    }

    public string Name { get; }
    public bool BuiltIn { get; }
    public IReadOnlyList<string> Values { get; }
    public IReadOnlyList<int> Weights { get; }
    public long TotalWeight { get; }

    public string Pick(Random random)
    {
        var target = (long)(random.NextDouble() * TotalWeight);
        var index = Array.BinarySearch(_cumulative, target + 1);
        if (index < 0)
        {
            index = ~index;
        }

        return Values[Math.Min(index, Values.Count - 1)];
    }
}

/// <summary>
/// 字典存放在設定資料夾，每個字典一個 UTF-8 文字檔：一行一個值，可在值後加 Tab 與權重（1–1,000,000），# 開頭為註解。
/// 檔案格式與 Windows 版相同，可直接互通。內建字典唯讀。
/// </summary>
public static class DataGeneratorDictionaryStore
{
    public const string FileExtension = ".txt";
    public const int MaximumEntries = 100_000;
    public const int MaximumValueLength = 4000;
    public const int MaximumWeight = 1_000_000;
    public const long MaximumFileBytes = 8L * 1024 * 1024;
    private static readonly Regex NamePattern = new(@"^[\p{L}\p{N}_\- ]{1,60}$", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> BuiltInSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh-TW 姓氏"] = "陳\t112\n林\t83\n黃\t60\n張\t53\n李\t51\n王\t41\n吳\t40\n劉\t32\n蔡\t29\n楊\t26\n許\t23\n鄭\t19\n謝\t18\n郭\t17\n洪\t16\n曾\t15\n邱\t15\n廖\t14\n賴\t14\n周\t13\n徐\t12\n蘇\t12\n葉\t12\n莊\t11\n呂\t10\n江\t10\n何\t10\n蕭\t9\n羅\t9\n高\t9",
        ["zh-TW 名字"] = "家豪\n志明\n俊傑\n建宏\n承恩\n宥廷\n冠宇\n柏翰\n品睿\n彥廷\n怡君\n雅婷\n淑芬\n佳穎\n詩涵\n欣妤\n子涵\n宜蓁\n思妤\n語彤",
        ["zh-TW 縣市"] = "臺北市\t25\n新北市\t40\n桃園市\t23\n臺中市\t28\n臺南市\t18\n高雄市\t27\n基隆市\t4\n新竹市\t4\n新竹縣\t6\n苗栗縣\t5\n彰化縣\t12\n南投縣\t5\n雲林縣\t7\n嘉義市\t3\n嘉義縣\t5\n屏東縣\t8\n宜蘭縣\t4\n花蓮縣\t3\n臺東縣\t2\n澎湖縣\t1\n金門縣\t1\n連江縣\t1",
        ["en first names"] = "James\nMary\nJohn\nPatricia\nRobert\nJennifer\nMichael\nLinda\nWilliam\nElizabeth\nDavid\nBarbara\nRichard\nSusan\nJoseph\nJessica\nThomas\nSarah\nCharles\nKaren\nDaniel\nNancy\nMatthew\nLisa\nAnthony\nBetty\nMark\nSandra\nSteven\nAshley",
        ["en last names"] = "Smith\nJohnson\nWilliams\nBrown\nJones\nGarcia\nMiller\nDavis\nRodriguez\nMartinez\nHernandez\nLopez\nGonzalez\nWilson\nAnderson\nThomas\nTaylor\nMoore\nJackson\nMartin\nLee\nPerez\nThompson\nWhite\nHarris",
        ["order status"] = "pending\t10\npaid\t50\nshipped\t25\ndelivered\t60\ncancelled\t5\nrefunded\t2"
    };

    public static string DefaultDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.GetTempPath();
            }

            return Path.Combine(root, "mySQLPunk", "datagen-dictionaries");
        }
    }

    public static bool IsBuiltIn(string? name) => name is not null && BuiltInSources.ContainsKey(name);

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name == name.Trim() && NamePattern.IsMatch(name);

    public static DataGeneratorDictionary Parse(string name, string? text, bool builtIn = false)
    {
        var values = new List<string>();
        var weights = new List<int>();
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var value = line;
            var weight = 1;
            var tab = line.LastIndexOf('\t');
            if (tab >= 0)
            {
                if (!int.TryParse(line[(tab + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out weight) || weight < 1 || weight > MaximumWeight)
                {
                    throw new InvalidOperationException($"字典「{name}」第 {index + 1} 行的權重必須是 1 到 {MaximumWeight} 的整數。");
                }

                value = line[..tab];
            }

            value = value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (value.Length > MaximumValueLength)
            {
                throw new InvalidOperationException($"字典「{name}」第 {index + 1} 行超過 {MaximumValueLength} 個字元。");
            }

            if (value.Any(char.IsControl))
            {
                throw new InvalidOperationException($"字典「{name}」第 {index + 1} 行含有控制字元。");
            }

            values.Add(value);
            weights.Add(weight);
            if (values.Count > MaximumEntries)
            {
                throw new InvalidOperationException($"字典「{name}」最多 {MaximumEntries:N0} 個值。");
            }
        }

        return new DataGeneratorDictionary(name, values, weights, builtIn);
    }

    public static string Serialize(DataGeneratorDictionary dictionary)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < dictionary.Values.Count; i++)
        {
            builder.Append(dictionary.Values[i]);
            if (dictionary.Weights[i] != 1)
            {
                builder.Append('\t').Append(dictionary.Weights[i].ToString(CultureInfo.InvariantCulture));
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    public static IReadOnlyList<string> List(string directory)
    {
        var names = BuiltInSources.Keys.ToList();
        if (Directory.Exists(directory))
        {
            names.AddRange(Directory.GetFiles(directory, "*" + FileExtension)
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(name => IsValidName(name) && !BuiltInSources.ContainsKey(name))
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase));
        }

        return names;
    }

    /// <summary>載入字典；找不到時回傳 null。</summary>
    public static DataGeneratorDictionary? Load(string directory, string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (BuiltInSources.TryGetValue(trimmed, out var source))
        {
            return Parse(trimmed, source, builtIn: true);
        }

        if (!IsValidName(trimmed))
        {
            return null;
        }

        var path = Path.Combine(directory, trimmed + FileExtension);
        if (!File.Exists(path))
        {
            return null;
        }

        if (new FileInfo(path).Length > MaximumFileBytes)
        {
            throw new InvalidOperationException($"字典檔「{trimmed}」超過 {MaximumFileBytes / (1024 * 1024)} MB。");
        }

        return Parse(trimmed, File.ReadAllText(path, Encoding.UTF8));
    }

    public static DataGeneratorDictionary Save(string directory, string? name, string? text)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (!IsValidName(trimmed))
        {
            throw new InvalidOperationException("字典名稱只能包含文字、數字、空白、底線與連字號（最多 60 字）。");
        }

        if (IsBuiltIn(trimmed))
        {
            throw new InvalidOperationException($"「{trimmed}」是內建字典，不能修改或刪除。");
        }

        var dictionary = Parse(trimmed, text);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, trimmed + FileExtension);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Serialize(dictionary), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        return dictionary;
    }

    public static void Delete(string directory, string? name)
    {
        if (IsBuiltIn(name))
        {
            throw new InvalidOperationException($"「{name}」是內建字典，不能修改或刪除。");
        }

        if (!IsValidName(name))
        {
            return;
        }

        var path = Path.Combine(directory, name + FileExtension);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
