using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;

namespace mySQLPunk.lib
{
    public enum DataGeneratorRuleKind
    {
        Auto,
        DatabaseDefault,
        Null,
        Fixed,
        Sequence,
        Range,
        List,
        Pattern,
        /// <summary>依權重從字典挑選；Text 為字典名稱。</summary>
        Dictionary
    }

    public sealed class DataGeneratorRule
    {
        public DataGeneratorRule(DataGeneratorRuleKind kind, string text = "", int nullPercent = 0, DataGeneratorDictionary dictionary = null)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            NullPercent = nullPercent;
            Dictionary = dictionary;
        }

        public static DataGeneratorRule Auto { get { return new DataGeneratorRule(DataGeneratorRuleKind.Auto); } }

        public DataGeneratorRuleKind Kind { get; private set; }
        public string Text { get; private set; }
        public int NullPercent { get; private set; }
        /// <summary>Dictionary 規則在產生前解析好的字典內容；null 代表找不到。</summary>
        public DataGeneratorDictionary Dictionary { get; private set; }

        public bool IsDefault { get { return Kind == DataGeneratorRuleKind.Auto && Text.Length == 0 && NullPercent == 0; } }
    }

    public enum GeneratedValueKind
    {
        Integer,
        Boolean,
        Decimal,
        Date,
        DateTime,
        DateTimeOffset,
        Time,
        Year,
        Guid,
        String,
        Json,
        Xml,
        Binary,
        NetworkAddress,
        Unsupported
    }

    /// <summary>一個欄位的產生資訊；由 provider 包裝層從目錄查詢整理而來。</summary>
    public sealed class DataGeneratorColumn
    {
        public DataGeneratorColumn()
        {
            EnumValues = new List<string>();
        }

        public string Name { get; set; }
        public string TypeText { get; set; }
        public int Ordinal { get; set; }
        public GeneratedValueKind Kind { get; set; }
        public bool IsNullable { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsAutoNumber { get; set; }
        public bool IsComputed { get; set; }
        public bool HasDefault { get; set; }
        public int? MaxLength { get; set; }
        public int? Precision { get; set; }
        public int? Scale { get; set; }
        public long IntegerMinimum { get; set; }
        public long IntegerMaximum { get; set; }
        public int? FixedBinaryLength { get; set; }
        public List<string> EnumValues { get; private set; }
        public bool IsSet { get; set; }
        /// <summary>true 時日期時間以文字寫入（PostgreSQL 未指定型別參數、SQLite 文字格式）；否則寫入 DateTime 等物件。</summary>
        public bool TemporalAsText { get; set; }
        /// <summary>SQLite 沒有布林型別，以 0／1 整數寫入。</summary>
        public bool BooleanAsInteger { get; set; }
    }

    public sealed class DataGeneratorForeignKey
    {
        public string Name { get; set; }
        public List<string> Columns { get; set; }
        public string ParentTable { get; set; }
        public List<string> ParentColumns { get; set; }
    }

    public sealed class DataGeneratorTable
    {
        public DataGeneratorTable()
        {
            Columns = new List<DataGeneratorColumn>();
            ForeignKeys = new List<DataGeneratorForeignKey>();
            UniqueSets = new List<string[]>();
        }

        public string Name { get; set; }
        public List<DataGeneratorColumn> Columns { get; private set; }
        public List<DataGeneratorForeignKey> ForeignKeys { get; private set; }
        public List<string[]> UniqueSets { get; private set; }
        public DataTable ExistingRows { get; set; }
        public bool ExistingTruncated { get; set; }
    }

    public sealed class DataGeneratorPlan
    {
        public DataGeneratorPlan(string tableName, int rowCount, IDictionary<string, DataGeneratorRule> rules)
        {
            TableName = tableName;
            RowCount = rowCount;
            Rules = new Dictionary<string, DataGeneratorRule>(rules ?? new Dictionary<string, DataGeneratorRule>(), StringComparer.OrdinalIgnoreCase);
        }

        public string TableName { get; private set; }
        public int RowCount { get; private set; }
        public Dictionary<string, DataGeneratorRule> Rules { get; private set; }
    }

    public sealed class DataGenerationResult
    {
        public DataGenerationResult()
        {
            Tables = new List<DataTableComparison>();
            Warnings = new List<string>();
        }

        public List<DataTableComparison> Tables { get; private set; }
        public List<string> Warnings { get; private set; }
        public string Error { get; set; }
        public bool Succeeded { get { return Error == null; } }
        public int TotalRows { get { return Tables.Sum(table => table.Inserts); } }
    }

    /// <summary>
    /// 資料產生器的純核心：依欄位規則產生資料列。外鍵欄位只挑現有或同批產生的父列，主鍵與唯一組合不與既有或
    /// 同批資料重複；結果以 Insert 變更表示，寫入沿用資料同步的單一交易路徑。
    /// </summary>
    public static class DataGeneratorCore
    {
        public const int MaximumRowsPerTable = 100000;
        public const int MaximumTotalRows = 200000;
        public const int ExistingRowLimit = 100000;
        private const int MaximumAttempts = 200;
        private const string NullMarker = "\u0000NULL";

        private static readonly string[] FirstNames = { "Alice", "Bob", "Carol", "David", "Emma", "Frank", "Grace", "Henry", "Ivy", "Jack", "Karen", "Leo", "Mia", "Noah", "Olivia", "Peter", "Quinn", "Ruby", "Sam", "Tina", "Uma", "Victor", "Wendy", "Yuki" };
        private static readonly string[] LastNames = { "Chen", "Lin", "Wang", "Smith", "Johnson", "Brown", "Garcia", "Miller", "Davis", "Lopez", "Wilson", "Anderson", "Taylor", "Thomas", "Moore", "Martin", "Lee", "Walker", "Hall", "Young" };
        private static readonly string[] Cities = { "Taipei", "Taichung", "Kaohsiung", "Tainan", "Tokyo", "Osaka", "Seoul", "Singapore", "London", "Paris", "Berlin", "New York", "Chicago", "Toronto", "Sydney", "Madrid" };
        private static readonly string[] Countries = { "Taiwan", "Japan", "Korea", "Singapore", "United Kingdom", "France", "Germany", "United States", "Canada", "Australia", "Spain" };
        private static readonly string[] Streets = { "Main", "Oak", "Maple", "Park", "Lake", "Hill", "River", "Sunset", "Cedar", "Elm" };
        private static readonly string[] Words = { "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do", "eiusmod", "tempor", "incididunt", "labore", "dolore", "magna", "aliqua", "enim", "minim", "veniam" };
        private static readonly string[] Statuses = { "active", "inactive", "pending", "archived" };
        private static readonly DateTime TemporalBase = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        /// <summary>依 plans 的順序產生（呼叫端需先依外鍵相依排序）；loadTable 讀取任何需要的資料表（含只被參照的父表）。</summary>
        public static DataGenerationResult Generate(IList<DataGeneratorPlan> plans, Func<string, DataGeneratorTable> loadTable, int? seed)
        {
            DataGenerationResult result = new DataGenerationResult();
            if (plans == null || plans.Count == 0)
            {
                result.Error = Localization.T("DataGen.Error.NoTables");
                return result;
            }

            foreach (DataGeneratorPlan plan in plans)
            {
                if (plan.RowCount < 1 || plan.RowCount > MaximumRowsPerTable)
                {
                    result.Error = Localization.Format("DataGen.Error.RowCount", plan.TableName, MaximumRowsPerTable);
                    return result;
                }
            }

            if (plans.Sum(plan => (long)plan.RowCount) > MaximumTotalRows)
            {
                result.Error = Localization.Format("DataGen.Error.TotalRows", MaximumTotalRows);
                return result;
            }

            Random random = seed.HasValue ? new Random(seed.Value) : new Random();
            Dictionary<string, TableState> states = new Dictionary<string, TableState>(StringComparer.OrdinalIgnoreCase);
            Func<string, TableState> state = name =>
            {
                TableState found;
                if (!states.TryGetValue(name, out found))
                {
                    DataGeneratorTable loaded = loadTable(name);
                    if (loaded == null) throw new GenerationException(Localization.Format("DataGen.Error.TableMissing", name));
                    found = new TableState(loaded);
                    states[name] = found;
                }
                return found;
            };

            try
            {
                // 先登記所有外鍵，父表產生的資料列才會收進子表挑選的集合。
                Dictionary<string, List<Link>> links = new Dictionary<string, List<Link>>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DataGeneratorPlan plan in plans)
                {
                    TableState child = state(plan.TableName);
                    List<Link> list = new List<Link>();
                    links[plan.TableName] = list;
                    foreach (DataGeneratorForeignKey foreignKey in child.Table.ForeignKeys)
                    {
                        if (foreignKey.Columns.Count == 0 || foreignKey.Columns.Count != foreignKey.ParentColumns.Count ||
                            foreignKey.Columns.Any(name => child.Find(name) == null))
                        {
                            result.Warnings.Add(Localization.Format("DataGen.Warn.ForeignKeyUnmapped", plan.TableName, foreignKey.Name));
                            continue;
                        }

                        TableState parent = state(foreignKey.ParentTable);
                        List<DataGeneratorColumn> parentColumns = foreignKey.ParentColumns.Select(parent.Find).ToList();
                        if (parentColumns.Any(column => column == null))
                        {
                            throw new GenerationException(Localization.Format("DataGen.Error.ParentColumnMissing", foreignKey.ParentTable, foreignKey.Name));
                        }

                        list.Add(new Link(foreignKey, parent, parent.RegisterPool(parentColumns)));
                        foreach (DataGeneratorColumn column in parentColumns)
                        {
                            referenced.Add(parent.Table.Name + "\u0001" + column.Name);
                        }
                    }
                }

                foreach (TableState item in states.Values.Where(item => item.Table.ExistingTruncated))
                {
                    result.Warnings.Add(Localization.Format("DataGen.Warn.ExistingTruncated", item.Table.Name, ExistingRowLimit));
                }

                foreach (DataGeneratorPlan plan in plans)
                {
                    try
                    {
                        result.Tables.Add(GenerateTable(plan, state(plan.TableName), links[plan.TableName], referenced, random, result.Warnings));
                    }
                    catch (GenerationException exception)
                    {
                        throw new GenerationException(plan.TableName + ": " + exception.Message);
                    }
                }
            }
            catch (GenerationException exception)
            {
                result.Tables.Clear();
                result.Error = exception.Message;
            }

            return result;
        }

        /// <summary>Auto 規則會做什麼，供規則編輯畫面顯示。</summary>
        public static string DescribeAuto(DataGeneratorColumn column, bool isUnique, DataGeneratorForeignKey foreignKey)
        {
            if (column.IsComputed) return Localization.T("DataGen.Auto.Computed");
            if (column.IsAutoNumber) return Localization.T("DataGen.Auto.AutoNumber");
            if (foreignKey != null) return Localization.Format("DataGen.Auto.ForeignKey", foreignKey.ParentTable, string.Join(", ", foreignKey.ParentColumns));
            string description;
            Func<Random, object> generator = BuildAutoGenerator(column, isUnique, 0m, 0, out description);
            if (generator == null)
            {
                return column.IsNullable || column.HasDefault ? Localization.T("DataGen.Auto.UnsupportedOmit") : Localization.T("DataGen.Auto.UnsupportedRequired");
            }
            return description;
        }

        private static DataTableComparison GenerateTable(
            DataGeneratorPlan plan,
            TableState state,
            List<Link> links,
            HashSet<string> referenced,
            Random random,
            List<string> warnings)
        {
            Func<DataGeneratorColumn, DataGeneratorRule> ruleFor = column =>
            {
                DataGeneratorRule rule;
                return plan.Rules.TryGetValue(column.Name, out rule) && rule != null ? rule : DataGeneratorRule.Auto;
            };

            foreach (string name in plan.Rules.Keys)
            {
                if (state.Find(name) == null) throw new GenerationException(Localization.Format("DataGen.Error.UnknownColumn", name));
            }

            List<string[]> uniqueSets = state.Table.UniqueSets;
            HashSet<string> uniqueSingles = new HashSet<string>(uniqueSets.Where(set => set.Length == 1).Select(set => set[0]), StringComparer.OrdinalIgnoreCase);
            List<Link> activeLinks = links.Where(link => link.ForeignKey.Columns.All(name => ruleFor(state.Find(name)).Kind == DataGeneratorRuleKind.Auto)).ToList();
            HashSet<string> linkedColumns = new HashSet<string>(activeLinks.SelectMany(link => link.ForeignKey.Columns), StringComparer.OrdinalIgnoreCase);

            List<ColumnGenerator> generators = new List<ColumnGenerator>();
            foreach (DataGeneratorColumn column in state.Table.Columns.OrderBy(item => item.Ordinal))
            {
                DataGeneratorRule rule = ruleFor(column);
                if (rule.NullPercent < 0 || rule.NullPercent > 100) throw new GenerationException(Localization.Format("DataGen.Error.NullPercent", column.Name));
                if (column.IsComputed)
                {
                    if (rule.Kind != DataGeneratorRuleKind.Auto && rule.Kind != DataGeneratorRuleKind.DatabaseDefault)
                    {
                        throw new GenerationException(Localization.Format("DataGen.Error.Computed", column.Name));
                    }
                    continue;
                }

                if (linkedColumns.Contains(column.Name)) continue;
                if (rule.Kind == DataGeneratorRuleKind.DatabaseDefault ||
                    rule.Kind == DataGeneratorRuleKind.Auto && column.IsAutoNumber && !referenced.Contains(state.Table.Name + "\u0001" + column.Name))
                {
                    if (!column.IsNullable && !column.HasDefault && !column.IsAutoNumber)
                    {
                        throw new GenerationException(Localization.Format("DataGen.Error.NoDefault", column.Name));
                    }
                    continue;
                }

                bool unique = uniqueSingles.Contains(column.Name);
                Func<Random, object> next;
                if (rule.Kind == DataGeneratorRuleKind.Auto)
                {
                    string ignored;
                    next = BuildAutoGenerator(column, unique, state.MaximumNumber(column), state.ExistingRowCount, out ignored);
                    if (next == null)
                    {
                        if (column.IsNullable || column.HasDefault) continue;
                        throw new GenerationException(Localization.Format("DataGen.Error.Unsupported", column.Name, column.TypeText));
                    }
                }
                else
                {
                    if (rule.Kind == DataGeneratorRuleKind.Null && !column.IsNullable)
                    {
                        throw new GenerationException(Localization.Format("DataGen.Error.NotNull", column.Name));
                    }
                    next = BuildRuleGenerator(column, rule);
                }

                int nullPercent = column.IsNullable && !unique && !column.IsPrimaryKey ? rule.NullPercent : 0;
                generators.Add(new ColumnGenerator(column, next, nullPercent));
            }

            foreach (Link link in activeLinks)
            {
                if (link.Pool.Tuples.Count == 0 && link.Parent != state &&
                    !link.ForeignKey.Columns.All(name => state.Find(name).IsNullable))
                {
                    throw new GenerationException(Localization.Format("DataGen.Error.ParentEmpty", link.ForeignKey.Name, link.Parent.Table.Name));
                }
            }

            HashSet<string> written = new HashSet<string>(
                generators.Select(item => item.Column.Name).Concat(activeLinks.SelectMany(link => link.ForeignKey.Columns)),
                StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string[], HashSet<string>>> checkedSets = uniqueSets
                .Where(set => set.All(written.Contains))
                .Select(set => new KeyValuePair<string[], HashSet<string>>(set, state.ExistingTuples(set)))
                .ToList();
            List<string> keyColumns = state.Table.Columns.Where(column => column.IsPrimaryKey).OrderBy(column => column.Ordinal).Select(column => column.Name).ToList();
            HashSet<string> nullWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DataTableComparison table = new DataTableComparison { TableName = state.Table.Name };
            table.KeyColumns.AddRange(keyColumns);

            for (int rowNumber = 1; rowNumber <= plan.RowCount; rowNumber++)
            {
                Dictionary<string, object> values = null;
                List<string> keys = null;
                for (int attempt = 0; attempt < MaximumAttempts && keys == null; attempt++)
                {
                    values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (Link link in activeLinks)
                    {
                        object[] tuple = link.Pool.Tuples.Count == 0 ? null : link.Pool.Tuples[random.Next(link.Pool.Tuples.Count)];
                        int nullPercent = link.ForeignKey.Columns.Max(name => ruleFor(state.Find(name)).NullPercent);
                        bool allNullable = link.ForeignKey.Columns.All(name => state.Find(name).IsNullable);
                        if (tuple == null || allNullable && nullPercent > 0 && random.Next(100) < nullPercent)
                        {
                            if (tuple == null && !allNullable)
                            {
                                throw new GenerationException(Localization.Format("DataGen.Error.ParentEmptyRow", rowNumber, link.ForeignKey.Name));
                            }
                            if (tuple == null && link.Parent != state && nullWarned.Add(link.ForeignKey.Name))
                            {
                                warnings.Add(Localization.Format("DataGen.Warn.ParentEmptyNull", state.Table.Name, link.ForeignKey.Name, link.Parent.Table.Name));
                            }
                            foreach (string name in link.ForeignKey.Columns) values[state.Find(name).Name] = DBNull.Value;
                            continue;
                        }

                        for (int index = 0; index < link.ForeignKey.Columns.Count; index++)
                        {
                            values[state.Find(link.ForeignKey.Columns[index]).Name] = tuple[index];
                        }
                    }

                    foreach (ColumnGenerator generator in generators)
                    {
                        object value = generator.NullPercent > 0 && random.Next(100) < generator.NullPercent ? null : generator.Next(random);
                        if (value == null && !generator.Column.IsNullable)
                        {
                            throw new GenerationException(Localization.Format("DataGen.Error.NotNull", generator.Column.Name));
                        }
                        values[generator.Column.Name] = value ?? DBNull.Value;
                    }

                    List<string> candidate = new List<string>();
                    bool collides = false;
                    foreach (KeyValuePair<string[], HashSet<string>> set in checkedSets)
                    {
                        List<string> parts = set.Key.Select(name => Canonical(values[state.Find(name).Name])).ToList();
                        if (parts.Contains(NullMarker))
                        {
                            candidate.Add(string.Empty);
                            continue;
                        }

                        string key = string.Join("\u0001", parts);
                        if (set.Value.Contains(key))
                        {
                            collides = true;
                            break;
                        }
                        candidate.Add(key);
                    }

                    if (!collides) keys = candidate;
                }

                if (keys == null)
                {
                    throw new GenerationException(Localization.Format("DataGen.Error.Exhausted", rowNumber, MaximumAttempts,
                        string.Join("; ", checkedSets.Select(set => string.Join(", ", set.Key)))));
                }

                for (int index = 0; index < checkedSets.Count; index++)
                {
                    if (keys[index].Length > 0) checkedSets[index].Value.Add(keys[index]);
                }

                Dictionary<string, object> ordered = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (DataGeneratorColumn column in state.Table.Columns.OrderBy(item => item.Ordinal).Where(item => values.ContainsKey(item.Name)))
                {
                    ordered[column.Name] = values[column.Name];
                }

                table.Changes.Add(new DataRowChange
                {
                    Kind = DataRowChangeKind.Insert,
                    KeyText = keyColumns.Count > 0 && keyColumns.All(values.ContainsKey)
                        ? string.Join(",", keyColumns.Select(name => System.Convert.ToString(values[name], CultureInfo.InvariantCulture)))
                        : "#" + rowNumber.ToString(CultureInfo.InvariantCulture),
                    Values = ordered
                });
                state.AddGeneratedRow(ordered);
            }

            if (table.Changes.Count > 0) table.SyncColumns.AddRange(table.Changes[0].Values.Keys);
            return table;
        }

        // ------------------------------------------------------------ Auto

        internal static Func<Random, object> BuildAutoGenerator(DataGeneratorColumn column, bool unique, decimal existingMaximum, int counterStart, out string description)
        {
            string name = new string((column.Name ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            switch (column.Kind)
            {
                case GeneratedValueKind.Integer:
                {
                    long minimum = column.IntegerMinimum;
                    long maximum = column.IntegerMaximum;
                    if (unique)
                    {
                        long nextValue = (long)Math.Max(Math.Max(existingMaximum, 0m) + 1m, minimum);
                        description = Localization.T("DataGen.Auto.UniqueInteger");
                        return _ =>
                        {
                            if (nextValue > maximum) throw new GenerationException(Localization.Format("DataGen.Error.IntegerOverflow", column.Name, maximum));
                            return nextValue++;
                        };
                    }

                    long low = 1, high = 1000;
                    description = Localization.Format("DataGen.Auto.RandomInteger", 1, 1000);
                    if (name.Contains("age")) { low = 18; high = 80; }
                    else if (name.Contains("year")) { low = 1990; high = 2030; }
                    else if (name.Contains("qty") || name.Contains("quantity") || name.Contains("count") || name.Contains("stock")) { low = 0; high = 100; }
                    else if (name.Contains("status") || name.Contains("level") || name.Contains("type") || name.Contains("flag")) { low = 0; high = 5; }
                    low = Math.Max(low, minimum);
                    high = Math.Min(high, maximum);
                    if (low > high)
                    {
                        low = Math.Max(0, minimum);
                        high = Math.Min(maximum, Math.Max(low, 1));
                    }
                    if (low != 1 || high != 1000) description = Localization.Format("DataGen.Auto.RandomInteger", low, high);
                    long from = low, to = high;
                    return r => from + (long)(r.NextDouble() * (to - from + 1));
                }

                case GeneratedValueKind.Boolean:
                    description = Localization.T("DataGen.Auto.Boolean");
                    if (column.BooleanAsInteger) return r => (object)(long)r.Next(2);
                    return r => r.Next(2) == 1;

                case GeneratedValueKind.Decimal:
                {
                    int scale = Math.Min(column.Scale ?? 2, 2);
                    decimal maximum = name.Contains("price") || name.Contains("amount") || name.Contains("cost") || name.Contains("total") || name.Contains("salary") ? 9999m : 1000m;
                    if (column.Precision.HasValue && column.Scale.HasValue)
                    {
                        int integerDigits = column.Precision.Value - column.Scale.Value;
                        maximum = integerDigits <= 0 ? 0m : Math.Min(maximum, Pow10(Math.Min(integerDigits, 18)) - 1m);
                    }
                    if (unique)
                    {
                        decimal nextValue = decimal.Floor(Math.Max(existingMaximum, 0m)) + 1m;
                        description = Localization.T("DataGen.Auto.UniqueDecimal");
                        return _ => nextValue++;
                    }
                    description = Localization.Format("DataGen.Auto.RandomDecimal", maximum.ToString("N0", CultureInfo.InvariantCulture));
                    long steps = (long)(maximum * Pow10(scale));
                    decimal factor = Pow10(scale);
                    return r => (decimal)(long)(r.NextDouble() * (steps + 1)) / factor;
                }

                case GeneratedValueKind.Date:
                case GeneratedValueKind.DateTime:
                case GeneratedValueKind.DateTimeOffset:
                case GeneratedValueKind.Time:
                {
                    bool birth = name.Contains("birth") || name.Contains("dob");
                    if (unique)
                    {
                        long step = counterStart;
                        description = Localization.T("DataGen.Auto.UniqueTemporal");
                        return _ =>
                        {
                            if (column.Kind == GeneratedValueKind.Time && step >= 86400)
                            {
                                throw new GenerationException(Localization.Format("DataGen.Error.TimeExhausted", column.Name));
                            }
                            DateTime value = column.Kind == GeneratedValueKind.Date ? TemporalBase.AddDays(step)
                                : column.Kind == GeneratedValueKind.Time ? TemporalBase.AddSeconds(step)
                                : TemporalBase.AddMinutes(step);
                            step++;
                            return Temporal(column, value);
                        };
                    }

                    DateTime start = birth ? new DateTime(1960, 1, 1) : TemporalBase;
                    int days = birth ? 16000 : 2190;
                    description = birth ? Localization.T("DataGen.Auto.Birthday") : Localization.T("DataGen.Auto.RandomTemporal");
                    return r => Temporal(column, start.AddDays(r.Next(days)).AddMinutes(column.Kind == GeneratedValueKind.Date ? 0 : r.Next(24 * 60)));
                }

                case GeneratedValueKind.Year:
                    if (unique)
                    {
                        int year = Math.Max(1901, (int)Math.Min(existingMaximum, 2154m) + 1);
                        description = Localization.T("DataGen.Auto.UniqueYear");
                        return _ =>
                        {
                            if (year > 2155) throw new GenerationException(Localization.Format("DataGen.Error.IntegerOverflow", column.Name, 2155));
                            return year++;
                        };
                    }
                    description = Localization.Format("DataGen.Auto.RandomInteger", 1990, 2030);
                    return r => r.Next(1990, 2031);

                case GeneratedValueKind.Guid:
                    description = Localization.T("DataGen.Auto.Guid");
                    if (column.TemporalAsText) return r => NewGuid(r).ToString("D");
                    return r => NewGuid(r);

                case GeneratedValueKind.String:
                    return BuildStringGenerator(column, name, unique, counterStart, out description);

                case GeneratedValueKind.Json:
                {
                    int counter = counterStart + 1;
                    description = Localization.T("DataGen.Auto.Json");
                    return _ =>
                    {
                        string id = (counter++).ToString(CultureInfo.InvariantCulture);
                        return "{\"id\":" + id + ",\"label\":\"sample " + id + "\"}";
                    };
                }

                case GeneratedValueKind.Xml:
                {
                    int counter = counterStart + 1;
                    description = Localization.T("DataGen.Auto.Xml");
                    return _ => "<item id=\"" + (counter++).ToString(CultureInfo.InvariantCulture) + "\" />";
                }

                case GeneratedValueKind.Binary:
                {
                    int length = column.FixedBinaryLength ?? Math.Max(1, Math.Min(column.MaxLength ?? 8, 8));
                    long counter = counterStart;
                    description = Localization.Format("DataGen.Auto.Binary", length);
                    return r =>
                    {
                        byte[] bytes = new byte[length];
                        r.NextBytes(bytes);
                        if (unique)
                        {
                            long value = counter++;
                            for (int index = length - 1; index >= 0 && index >= length - 8; index--)
                            {
                                bytes[index] = (byte)(value & 0xFF);
                                value >>= 8;
                            }
                        }
                        return bytes;
                    };
                }

                case GeneratedValueKind.NetworkAddress:
                {
                    int counter = counterStart;
                    description = Localization.T("DataGen.Auto.Network");
                    return r =>
                    {
                        int value = unique ? counter++ : r.Next(1 << 24);
                        return "10." + ((value >> 16) & 0xFF).ToString(CultureInfo.InvariantCulture) + "." +
                               ((value >> 8) & 0xFF).ToString(CultureInfo.InvariantCulture) + "." + (value & 0xFF).ToString(CultureInfo.InvariantCulture);
                    };
                }

                default:
                    description = string.Empty;
                    return null;
            }
        }

        private static Func<Random, object> BuildStringGenerator(DataGeneratorColumn column, string name, bool unique, int counterStart, out string description)
        {
            if (column.EnumValues.Count > 0)
            {
                List<string> members = column.EnumValues;
                description = Localization.T(column.IsSet ? "DataGen.Auto.Set" : "DataGen.Auto.Enum");
                return r => members[r.Next(members.Count)];
            }

            Func<Random, int, string> produce;
            if (name.Contains("email") || name.Contains("mail"))
            {
                description = Localization.T("DataGen.Auto.Email");
                produce = (_, n) => "user" + n.ToString(CultureInfo.InvariantCulture) + "@example.com";
            }
            else if (name.Contains("firstname") || name.Contains("givenname"))
            {
                description = Localization.T("DataGen.Auto.FirstName");
                produce = (r, _) => FirstNames[r.Next(FirstNames.Length)];
            }
            else if (name.Contains("lastname") || name.Contains("surname") || name.Contains("familyname"))
            {
                description = Localization.T("DataGen.Auto.LastName");
                produce = (r, _) => LastNames[r.Next(LastNames.Length)];
            }
            else if (name.Contains("username") || name.Contains("login") || name.Contains("account"))
            {
                description = Localization.T("DataGen.Auto.UserName");
                produce = (r, n) => FirstNames[r.Next(FirstNames.Length)].ToLowerInvariant() + n.ToString(CultureInfo.InvariantCulture);
            }
            else if (name.Contains("phone") || name.Contains("mobile") || name.Contains("tel"))
            {
                description = Localization.T("DataGen.Auto.Phone");
                produce = (r, _) => "09" + ((long)(r.NextDouble() * 100000000)).ToString("D8", CultureInfo.InvariantCulture);
            }
            else if (name.Contains("city"))
            {
                description = Localization.T("DataGen.Auto.City");
                produce = (r, _) => Cities[r.Next(Cities.Length)];
            }
            else if (name.Contains("country"))
            {
                description = Localization.T("DataGen.Auto.Country");
                produce = (r, _) => Countries[r.Next(Countries.Length)];
            }
            else if (name.Contains("address") || name.Contains("street"))
            {
                description = Localization.T("DataGen.Auto.Address");
                produce = (r, _) => r.Next(1, 999).ToString(CultureInfo.InvariantCulture) + " " + Streets[r.Next(Streets.Length)] + " St.";
            }
            else if (name.Contains("url") || name.Contains("website") || name.Contains("link"))
            {
                description = Localization.T("DataGen.Auto.Url");
                produce = (_, n) => "https://example.com/item/" + n.ToString(CultureInfo.InvariantCulture);
            }
            else if (name.Contains("name"))
            {
                description = Localization.T("DataGen.Auto.FullName");
                produce = (r, _) => FirstNames[r.Next(FirstNames.Length)] + " " + LastNames[r.Next(LastNames.Length)];
            }
            else if (name.Contains("status") || name.Contains("state"))
            {
                description = Localization.T("DataGen.Auto.Status");
                produce = (r, _) => Statuses[r.Next(Statuses.Length)];
            }
            else if (name.Contains("description") || name.Contains("comment") || name.Contains("note") ||
                     name.Contains("remark") || name.Contains("content") || name.Contains("body") || name.Contains("text"))
            {
                description = Localization.T("DataGen.Auto.Sentence");
                produce = (r, _) =>
                {
                    string[] words = Enumerable.Range(0, r.Next(4, 12)).Select(__ => Words[r.Next(Words.Length)]).ToArray();
                    words[0] = char.ToUpperInvariant(words[0][0]) + words[0].Substring(1);
                    return string.Join(" ", words) + ".";
                };
            }
            else if (name.Contains("title") || name.Contains("subject"))
            {
                description = Localization.T("DataGen.Auto.Title");
                produce = (_, n) => "Sample title " + n.ToString(CultureInfo.InvariantCulture);
            }
            else if (name.Contains("code") || name.Contains("sku") || name.Contains("no") && name.Length <= 8)
            {
                description = Localization.T("DataGen.Auto.Code");
                produce = (_, n) => "C" + n.ToString("D6", CultureInfo.InvariantCulture);
            }
            else
            {
                description = Localization.Format("DataGen.Auto.Text", column.Name);
                string prefix = column.Name + "_";
                produce = (_, n) => prefix + n.ToString(CultureInfo.InvariantCulture);
            }

            if (unique) description += Localization.T("DataGen.Auto.UniqueSuffix");
            int counter = counterStart + 1;
            int? limit = column.MaxLength;
            return r =>
            {
                int n = counter++;
                string number = n.ToString(CultureInfo.InvariantCulture);
                string text = produce(r, n);
                if (unique && text.IndexOf(number, StringComparison.Ordinal) < 0) text += "-" + number;
                text = text.TrimEnd();
                if (limit.HasValue && text.Length > limit.Value)
                {
                    if (!unique) return text.Substring(0, limit.Value).TrimEnd();
                    if (number.Length > limit.Value) throw new GenerationException(Localization.Format("DataGen.Error.StringExhausted", column.Name, limit.Value));
                    text = text.Substring(0, limit.Value - number.Length) + number;
                }
                return text;
            };
        }

        // ------------------------------------------------------------ Explicit rules

        internal static Func<Random, object> BuildRuleGenerator(DataGeneratorColumn column, DataGeneratorRule rule)
        {
            string text = rule.Text ?? string.Empty;
            switch (rule.Kind)
            {
                case DataGeneratorRuleKind.Null:
                    return _ => null;
                case DataGeneratorRuleKind.Fixed:
                    return _ => Convert(column, text);
                case DataGeneratorRuleKind.Dictionary:
                {
                    DataGeneratorDictionary dictionary = rule.Dictionary;
                    if (dictionary == null) throw new GenerationException(Localization.Format("DataGen.Error.DictionaryMissing", column.Name, text));
                    return r => Convert(column, dictionary.Pick(r));
                }

                case DataGeneratorRuleKind.List:
                {
                    string[] values = text.Split('|').Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
                    if (values.Length == 0) throw new GenerationException(Localization.Format("DataGen.Error.EmptyList", column.Name));
                    return r => Convert(column, values[r.Next(values.Length)]);
                }

                case DataGeneratorRuleKind.Sequence:
                {
                    string[] parts = text.Split(',').Select(part => part.Trim()).ToArray();
                    decimal step;
                    int stepScale;
                    if (parts.Length < 1 || parts.Length > 2 || parts[0].Length == 0 ||
                        !TryParseNumber(parts.Length > 1 ? parts[1] : "1", out step, out stepScale))
                    {
                        throw new GenerationException(Localization.Format("DataGen.Error.Sequence", column.Name));
                    }

                    decimal start;
                    int startScale;
                    if (TryParseNumber(parts[0], out start, out startScale))
                    {
                        decimal current = start;
                        return _ =>
                        {
                            decimal value = current;
                            current += step;
                            return NumberFor(column, value);
                        };
                    }

                    DateTime date;
                    string dateFormat;
                    if (TryParseDate(parts[0], out date, out dateFormat))
                    {
                        decimal index = 0m;
                        return _ => Temporal(column, date.AddDays((double)(step * index++)));
                    }

                    throw new GenerationException(Localization.Format("DataGen.Error.Sequence", column.Name));
                }

                case DataGeneratorRuleKind.Range:
                {
                    int separator = text.IndexOf("..", StringComparison.Ordinal);
                    if (separator < 0) throw new GenerationException(Localization.Format("DataGen.Error.Range", column.Name));
                    string lowText = text.Substring(0, separator).Trim();
                    string highText = text.Substring(separator + 2).Trim();
                    decimal low, high;
                    int lowScale, highScale;
                    if (TryParseNumber(lowText, out low, out lowScale) && TryParseNumber(highText, out high, out highScale))
                    {
                        if (low > high) throw new GenerationException(Localization.Format("DataGen.Error.RangeOrder", column.Name));
                        int scale = Math.Min(Math.Max(lowScale, highScale), 6);
                        decimal factor = Pow10(scale);
                        decimal steps = decimal.Floor((high - low) * factor);
                        return r => NumberFor(column, low + decimal.Floor((decimal)r.NextDouble() * (steps + 1)) / factor);
                    }

                    DateTime from, to;
                    string fromFormat, toFormat;
                    if (TryParseDate(lowText, out from, out fromFormat) && TryParseDate(highText, out to, out toFormat))
                    {
                        if (from > to) throw new GenerationException(Localization.Format("DataGen.Error.RangeOrder", column.Name));
                        bool dateOnly = fromFormat == "yyyy-MM-dd" && toFormat == "yyyy-MM-dd";
                        double span = dateOnly ? (to - from).TotalDays : (to - from).TotalMinutes;
                        return r =>
                        {
                            double offset = Math.Floor(r.NextDouble() * (span + 1));
                            return Temporal(column, dateOnly ? from.AddDays(offset) : from.AddMinutes(offset));
                        };
                    }

                    throw new GenerationException(Localization.Format("DataGen.Error.Range", column.Name));
                }

                case DataGeneratorRuleKind.Pattern:
                {
                    List<Func<Random, int, string>> parts = ParsePattern(column, text);
                    int counter = 1;
                    return r =>
                    {
                        int n = counter++;
                        StringBuilder builder = new StringBuilder();
                        foreach (Func<Random, int, string> part in parts) builder.Append(part(r, n));
                        return Convert(column, builder.ToString());
                    };
                }

                default:
                    throw new GenerationException(Localization.Format("DataGen.Error.Rule", column.Name));
            }
        }

        private static List<Func<Random, int, string>> ParsePattern(DataGeneratorColumn column, string text)
        {
            List<Func<Random, int, string>> parts = new List<Func<Random, int, string>>();
            StringBuilder literal = new StringBuilder();
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] != '{')
                {
                    literal.Append(text[index]);
                    continue;
                }

                int end = text.IndexOf('}', index);
                if (end < 0) throw new GenerationException(Localization.Format("DataGen.Error.PatternUnclosed", column.Name));
                if (literal.Length > 0)
                {
                    string fixedText = literal.ToString();
                    parts.Add((r, n) => fixedText);
                    literal.Clear();
                }

                string token = text.Substring(index + 1, end - index - 1);
                index = end;
                int colon = token.IndexOf(':');
                string tokenName = (colon < 0 ? token : token.Substring(0, colon)).Trim().ToLowerInvariant();
                string argument = colon < 0 ? string.Empty : token.Substring(colon + 1).Trim();
                int count;
                if (tokenName == "n" && argument.Length == 0)
                {
                    parts.Add((r, n) => n.ToString(CultureInfo.InvariantCulture));
                }
                else if (tokenName == "uuid" && argument.Length == 0)
                {
                    parts.Add((r, n) => NewGuid(r).ToString("D"));
                }
                else if ((tokenName == "digits" || tokenName == "letters") &&
                         int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= 1 && count <= 64)
                {
                    string alphabet = tokenName == "digits" ? "0123456789" : "abcdefghijklmnopqrstuvwxyz";
                    int length = count;
                    parts.Add((r, n) => new string(Enumerable.Range(0, length).Select(_ => alphabet[r.Next(alphabet.Length)]).ToArray()));
                }
                else if (tokenName == "int")
                {
                    string[] bounds = argument.Split(new[] { '-' }, 2);
                    long low, high;
                    if (bounds.Length != 2 ||
                        !long.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out low) ||
                        !long.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out high) ||
                        low > high)
                    {
                        throw new GenerationException(Localization.Format("DataGen.Error.PatternInt", column.Name));
                    }
                    parts.Add((r, n) => (low + (long)(r.NextDouble() * (high - low + 1))).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    throw new GenerationException(Localization.Format("DataGen.Error.PatternToken", column.Name, "{" + token + "}"));
                }
            }

            if (literal.Length > 0)
            {
                string fixedText = literal.ToString();
                parts.Add((r, n) => fixedText);
            }
            return parts;
        }

        // ------------------------------------------------------------ Helpers

        /// <summary>把規則文字轉成欄位型別的值；型別不合時在寫入前就失敗，而不是交給資料庫。</summary>
        private static object Convert(DataGeneratorColumn column, string text)
        {
            decimal number;
            int scale;
            DateTime date;
            string format;
            switch (column.Kind)
            {
                case GeneratedValueKind.Integer:
                case GeneratedValueKind.Decimal:
                case GeneratedValueKind.Year:
                    if (!TryParseNumber(text, out number, out scale)) throw new GenerationException(Localization.Format("DataGen.Error.NotNumber", column.Name, text));
                    return NumberFor(column, number);
                case GeneratedValueKind.Boolean:
                    string lowered = text.Trim().ToLowerInvariant();
                    bool flag;
                    if (lowered == "1" || lowered == "true") flag = true;
                    else if (lowered == "0" || lowered == "false") flag = false;
                    else throw new GenerationException(Localization.Format("DataGen.Error.NotBoolean", column.Name, text));
                    return column.BooleanAsInteger ? (object)(flag ? 1L : 0L) : flag;
                case GeneratedValueKind.Date:
                case GeneratedValueKind.DateTime:
                case GeneratedValueKind.DateTimeOffset:
                    if (!TryParseDate(text, out date, out format)) throw new GenerationException(Localization.Format("DataGen.Error.NotDate", column.Name, text));
                    return Temporal(column, date);
                case GeneratedValueKind.Guid:
                    Guid guid;
                    if (!Guid.TryParse(text, out guid)) throw new GenerationException(Localization.Format("DataGen.Error.NotGuid", column.Name, text));
                    return column.TemporalAsText ? (object)guid.ToString("D") : guid;
                case GeneratedValueKind.Binary:
                    return Encoding.UTF8.GetBytes(text);
                case GeneratedValueKind.String:
                    if (column.MaxLength.HasValue && text.Length > column.MaxLength.Value)
                    {
                        throw new GenerationException(Localization.Format("DataGen.Error.TooLong", column.Name, column.MaxLength.Value, text));
                    }
                    if (column.EnumValues.Count > 0 && !column.IsSet && !column.EnumValues.Contains(text, StringComparer.Ordinal))
                    {
                        throw new GenerationException(Localization.Format("DataGen.Error.NotEnum", column.Name, text));
                    }
                    return text;
                default:
                    return text;
            }
        }

        private static object NumberFor(DataGeneratorColumn column, decimal value)
        {
            if (column.Kind == GeneratedValueKind.Integer || column.Kind == GeneratedValueKind.Year)
            {
                if (decimal.Truncate(value) != value) throw new GenerationException(Localization.Format("DataGen.Error.NotInteger", column.Name, value));
                if (value < column.IntegerMinimum || value > column.IntegerMaximum)
                {
                    throw new GenerationException(Localization.Format("DataGen.Error.IntegerOverflow", column.Name, column.IntegerMaximum));
                }
                return column.Kind == GeneratedValueKind.Year ? (object)(int)value : (long)value;
            }
            if (column.Kind == GeneratedValueKind.String) return value.ToString(CultureInfo.InvariantCulture);
            return value;
        }

        /// <summary>日期時間值：MySQL／SQL Server 寫入 CLR 物件；PostgreSQL／SQLite 寫入 ISO 文字。</summary>
        private static object Temporal(DataGeneratorColumn column, DateTime value)
        {
            switch (column.Kind)
            {
                case GeneratedValueKind.Date:
                    return column.TemporalAsText ? (object)value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : value.Date;
                case GeneratedValueKind.Time:
                    return column.TemporalAsText ? (object)value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : value.TimeOfDay;
                case GeneratedValueKind.DateTimeOffset:
                    return column.TemporalAsText
                        ? (object)(value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00")
                        : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeSpan.Zero);
                case GeneratedValueKind.String:
                    return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                default:
                    return column.TemporalAsText ? (object)value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : value;
            }
        }

        /// <summary>唯一性比對用的正規化文字；大小寫不分，因 MySQL／SQL Server 預設定序視為重複。</summary>
        public static string Canonical(object value)
        {
            if (value == null || value is DBNull) return NullMarker;
            if (value is byte[]) return BitConverter.ToString((byte[])value);
            if (value is DateTime) return ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset) return ((DateTimeOffset)value).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            if (value is bool) return (bool)value ? "1" : "0";
            if (value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint ||
                value is long || value is ulong || value is decimal || value is float || value is double)
            {
                return System.Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("0.############################", CultureInfo.InvariantCulture);
            }
            string text = System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            DateTime parsed;
            string format;
            if (text.Length >= 10 && char.IsDigit(text[0]) && TryParseDate(text.Length > 19 ? text.Substring(0, 19) : text, out parsed, out format))
            {
                return parsed.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            }
            return text.ToUpperInvariant();
        }

        private static decimal Pow10(int exponent)
        {
            decimal value = 1m;
            for (int index = 0; index < exponent; index++) value *= 10m;
            return value;
        }

        private static bool TryParseNumber(string text, out decimal value, out int scale)
        {
            scale = 0;
            if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
            int dot = text.IndexOf('.');
            scale = dot < 0 ? 0 : text.Length - dot - 1;
            return true;
        }

        private static bool TryParseDate(string text, out DateTime value, out string format)
        {
            foreach (string candidate in new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd" })
            {
                if (DateTime.TryParseExact(text, candidate, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
                {
                    format = candidate;
                    return true;
                }
            }
            value = default(DateTime);
            format = string.Empty;
            return false;
        }

        private static Guid NewGuid(Random random)
        {
            byte[] bytes = new byte[16];
            random.NextBytes(bytes);
            bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new Guid(bytes);
        }

        private sealed class ColumnGenerator
        {
            public ColumnGenerator(DataGeneratorColumn column, Func<Random, object> next, int nullPercent)
            {
                Column = column;
                Next = next;
                NullPercent = nullPercent;
            }

            public DataGeneratorColumn Column { get; private set; }
            public Func<Random, object> Next { get; private set; }
            public int NullPercent { get; private set; }
        }

        private sealed class Link
        {
            public Link(DataGeneratorForeignKey foreignKey, TableState parent, TuplePool pool)
            {
                ForeignKey = foreignKey;
                Parent = parent;
                Pool = pool;
            }

            public DataGeneratorForeignKey ForeignKey { get; private set; }
            public TableState Parent { get; private set; }
            public TuplePool Pool { get; private set; }
        }

        private sealed class TuplePool
        {
            public TuplePool(List<DataGeneratorColumn> columns)
            {
                Columns = columns;
                Tuples = new List<object[]>();
            }

            public List<DataGeneratorColumn> Columns { get; private set; }
            public List<object[]> Tuples { get; private set; }
        }

        private sealed class TableState
        {
            private readonly List<TuplePool> pools = new List<TuplePool>();

            public TableState(DataGeneratorTable table)
            {
                Table = table;
            }

            public DataGeneratorTable Table { get; private set; }

            public int ExistingRowCount { get { return Table.ExistingRows == null ? 0 : Table.ExistingRows.Rows.Count; } }

            public DataGeneratorColumn Find(string name)
            {
                return Table.Columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            public TuplePool RegisterPool(List<DataGeneratorColumn> columns)
            {
                TuplePool existing = pools.FirstOrDefault(pool => pool.Columns.Select(column => column.Name)
                    .SequenceEqual(columns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase));
                if (existing != null) return existing;
                TuplePool created = new TuplePool(columns);
                foreach (DataRow row in Rows())
                {
                    object[] tuple = columns.Select(column => row.Table.Columns.Contains(column.Name) ? row[column.Name] : DBNull.Value).ToArray();
                    if (tuple.All(value => value != null && !(value is DBNull))) created.Tuples.Add(tuple);
                }
                pools.Add(created);
                return created;
            }

            /// <summary>產生的資料列可被子表挑選；由資料庫編號的鍵值未知，不加入。</summary>
            public void AddGeneratedRow(Dictionary<string, object> values)
            {
                foreach (TuplePool pool in pools)
                {
                    object[] tuple = new object[pool.Columns.Count];
                    bool complete = true;
                    for (int index = 0; index < pool.Columns.Count; index++)
                    {
                        object value;
                        if (!values.TryGetValue(pool.Columns[index].Name, out value) || value == null || value is DBNull)
                        {
                            complete = false;
                            break;
                        }
                        tuple[index] = value;
                    }
                    if (complete) pool.Tuples.Add(tuple);
                }
            }

            public HashSet<string> ExistingTuples(string[] set)
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (DataRow row in Rows())
                {
                    List<string> parts = set.Select(name => Canonical(row.Table.Columns.Contains(name) ? row[name] : null)).ToList();
                    if (!parts.Contains(NullMarker)) seen.Add(string.Join("\u0001", parts));
                }
                return seen;
            }

            public decimal MaximumNumber(DataGeneratorColumn column)
            {
                decimal maximum = 0m;
                foreach (DataRow row in Rows())
                {
                    if (!row.Table.Columns.Contains(column.Name)) break;
                    object value = row[column.Name];
                    decimal number;
                    if (value != null && !(value is DBNull) &&
                        decimal.TryParse(System.Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                        number > maximum)
                    {
                        maximum = number;
                    }
                }
                return maximum;
            }

            private IEnumerable<DataRow> Rows()
            {
                return Table.ExistingRows == null ? Enumerable.Empty<DataRow>() : Table.ExistingRows.Rows.Cast<DataRow>();
            }
        }

        private sealed class GenerationException : Exception
        {
            public GenerationException(string message) : base(message)
            {
            }
        }
    }
}
