using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    public sealed class ViewColumnPreference
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public bool Visible { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(DisplayName) ? (Name ?? string.Empty) : DisplayName;
        }
    }

    public static class ViewColumnPreferenceService
    {
        private static readonly string[] ProviderKeys =
        {
            "mysql", "postgresql", "oracle", "sqlite", "mssql", "mariadb", "mongodb", "redis", "snowflake", "dameng"
        };

        private static readonly Dictionary<string, string> ProviderDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "mysql", "MySQL" },
            { "postgresql", "PostgreSQL" },
            { "oracle", "Oracle" },
            { "sqlite", "SQLite" },
            { "mssql", "SQL Server" },
            { "mariadb", "MariaDB" },
            { "mongodb", "MongoDB" },
            { "redis", "Redis" },
            { "snowflake", "Snowflake" },
            { "dameng", "Dameng" }
        };

        private static readonly string[] GroupKeys =
        {
            "Tables", "Views", "Functions", "Events", "Users", "Tablespaces", "Queries"
        };

        private static readonly Dictionary<string, string> GroupDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Tables", "View.Group.Tables" },
            { "Views", "View.Group.Views" },
            { "Functions", "View.Group.Functions" },
            { "Events", "View.Group.Events" },
            { "Users", "View.Group.Users" },
            { "Tablespaces", "View.Group.Tablespaces" },
            { "Queries", "View.Group.Queries" }
        };

        private static readonly Dictionary<string, string> ColumnDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "群組", "View.Column.Group" },
            { "自動遞增值", "View.Column.AutoIncrement" },
            { "列格式", "View.Column.RowFormat" },
            { "修改日期", "View.Column.UpdateTime" },
            { "建立日期", "View.Column.CreateTime" },
            { "索引長度", "View.Column.IndexLength" },
            { "資料長度", "View.Column.DataLength" },
            { "引擎", "View.Column.Engine" },
            { "列", "View.Column.Rows" },
            { "最大資料長度", "View.Column.MaxDataLength" },
            { "資料可用空間", "View.Column.DataFree" },
            { "檢查時間", "View.Column.CheckTime" },
            { "定序", "View.Column.Collation" },
            { "建立選項", "View.Column.CreateOptions" },
            { "註解", "View.Column.Comment" },
            { "檢查選項", "View.Column.CheckOption" },
            { "定義者", "View.Column.Definer" },
            { "安全性類型", "View.Column.SecurityType" },
            { "是否可以更新", "View.Column.IsUpdatable" },
            { "函式類型", "View.Column.FunctionType" },
            { "具決定性", "View.Column.Deterministic" },
            { "資料存取", "View.Column.DataAccess" },
            { "事件重複類型", "View.Column.EventRepeatType" },
            { "狀態", "View.Column.Status" },
            { "執行時間", "View.Column.ExecuteAt" },
            { "間隔值", "View.Column.IntervalValue" },
            { "間隔欄位", "View.Column.IntervalField" },
            { "SSL 類型", "View.Column.SslType" },
            { "每小時最大查詢數目", "View.Column.MaxQuestionsPerHour" },
            { "每小時最大更新數目", "View.Column.MaxUpdatesPerHour" },
            { "每小時最大連線數目", "View.Column.MaxConnectionsPerHour" },
            { "最大使用者連線數目", "View.Column.MaxUserConnections" },
            { "超級使用者", "View.Column.Superuser" },
            { "類型", "View.Column.Type" },
            { "路徑", "View.Column.Path" },
            { "加密", "View.Column.Encrypted" },
            { "檔案大小", "View.Column.FileSize" },
            { "範圍大小", "View.Column.ExtentSize" },
            { "名稱", "View.Column.Name" },
            { "資料庫", "View.Column.Database" },
            { "編號", "View.Column.Id" }
        };

        private static readonly Dictionary<string, string[]> DefaultColumns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Tables", new[] { "群組", "自動遞增值", "列格式", "修改日期", "建立日期", "索引長度", "資料長度", "引擎", "列", "最大資料長度", "資料可用空間", "檢查時間", "定序", "建立選項", "註解" } },
            { "Views", new[] { "群組", "檢查選項", "定義者", "安全性類型", "是否可以更新" } },
            { "Functions", new[] { "群組", "定義者", "安全性類型", "修改日期", "函式類型", "具決定性", "建立日期", "資料存取", "註解" } },
            { "Events", new[] { "群組", "修改日期", "建立日期", "事件重複類型", "狀態", "執行時間", "間隔值", "間隔欄位", "STARTS", "ENDS", "ON COMPLETION", "註解" } },
            { "Users", new[] { "SSL 類型", "每小時最大查詢數目", "每小時最大更新數目", "每小時最大連線數目", "最大使用者連線數目", "超級使用者" } },
            { "Tablespaces", new[] { "引擎", "類型", "路徑", "狀態", "加密", "列格式", "檔案大小", "範圍大小" } },
            { "Queries", new[] { "名稱", "類型", "狀態", "資料庫", "編號", "SQL" } }
        };

        private static readonly Dictionary<string, HashSet<string>> DefaultVisibleColumns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "Tables", Visible("自動遞增值", "修改日期", "資料長度", "引擎", "列", "註解") },
            { "Views", Visible("是否可以更新") },
            { "Functions", Visible("修改日期", "函式類型", "具決定性", "註解") },
            { "Events", Visible("修改日期", "事件重複類型", "狀態", "執行時間", "註解") },
            { "Users", Visible("SSL 類型", "每小時最大查詢數目", "每小時最大更新數目", "每小時最大連線數目", "最大使用者連線數目", "超級使用者") },
            { "Tablespaces", Visible("引擎", "類型", "路徑", "狀態", "加密", "列格式", "檔案大小", "範圍大小") },
            { "Queries", Visible("名稱", "類型", "狀態", "資料庫", "編號", "SQL") }
        };

        public static IReadOnlyList<string> Providers => ProviderKeys;
        public static IReadOnlyList<string> Groups => GroupKeys;

        public static string GetProviderDisplayName(string provider)
        {
            string normalized = NormalizeProvider(provider);
            string display;
            return ProviderDisplayNames.TryGetValue(normalized, out display) ? display : normalized;
        }

        public static string GetGroupDisplayName(string groupKey)
        {
            string normalized = NormalizeGroup(groupKey);
            string key;
            return GroupDisplayNames.TryGetValue(normalized, out key) ? Localization.T(key) : normalized;
        }

        public static string GetColumnDisplayName(string columnName)
        {
            string key;
            string normalized = (columnName ?? string.Empty).Trim();
            return ColumnDisplayNames.TryGetValue(normalized, out key) ? Localization.T(key) : normalized;
        }

        public static string NormalizeProvider(string provider)
        {
            string value = (provider ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "sqlserver" || value == "sql server") return "mssql";
            if (value == "postgres" || value == "pgsql") return "postgresql";
            if (value.Length == 0) return "mysql";
            return ProviderDisplayNames.ContainsKey(value) ? value : "mysql";
        }

        public static string NormalizeGroup(string groupKey)
        {
            string value = (groupKey ?? string.Empty).Trim();
            if (value.Equals("Table", StringComparison.OrdinalIgnoreCase)) return "Tables";
            if (value.Equals("View", StringComparison.OrdinalIgnoreCase)) return "Views";
            if (value.Equals("Function", StringComparison.OrdinalIgnoreCase)) return "Functions";
            if (value.Equals("Event", StringComparison.OrdinalIgnoreCase)) return "Events";
            if (value.Equals("User", StringComparison.OrdinalIgnoreCase)) return "Users";
            if (value.Equals("Tablespace", StringComparison.OrdinalIgnoreCase)) return "Tablespaces";
            if (value.Equals("Query", StringComparison.OrdinalIgnoreCase)) return "Queries";
            return DefaultColumns.ContainsKey(value) ? value : "Tables";
        }

        public static List<ViewColumnPreference> Load(string provider, string groupKey)
        {
            string normalizedProvider = NormalizeProvider(provider);
            string normalizedGroup = NormalizeGroup(groupKey);
            List<ViewColumnPreference> defaults = BuildDefaults(normalizedGroup);
            string json = ApplicationOptionSettings.GetString(BuildOptionKey(normalizedProvider, normalizedGroup));
            if (string.IsNullOrWhiteSpace(json)) return defaults;

            try
            {
                List<ViewColumnPreference> saved = JsonConvert.DeserializeObject<List<ViewColumnPreference>>(json);
                if (saved == null || saved.Count == 0) return defaults;

                Dictionary<string, ViewColumnPreference> defaultMap = defaults.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
                List<ViewColumnPreference> merged = new List<ViewColumnPreference>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (ViewColumnPreference item in saved)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Name) || !defaultMap.ContainsKey(item.Name) || seen.Contains(item.Name)) continue;
                    merged.Add(new ViewColumnPreference { Name = item.Name, DisplayName = GetColumnDisplayName(item.Name), Visible = item.Visible });
                    seen.Add(item.Name);
                }

                foreach (ViewColumnPreference item in defaults)
                {
                    if (!seen.Contains(item.Name)) merged.Add(item);
                }

                return merged;
            }
            catch
            {
                return defaults;
            }
        }

        public static void Save(string provider, string groupKey, IEnumerable<ViewColumnPreference> preferences)
        {
            string normalizedProvider = NormalizeProvider(provider);
            string normalizedGroup = NormalizeGroup(groupKey);
            List<ViewColumnPreference> list = (preferences ?? Enumerable.Empty<ViewColumnPreference>())
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new ViewColumnPreference { Name = p.Name, Visible = p.Visible })
                .ToList();
            ApplicationOptionSettings.SetString(BuildOptionKey(normalizedProvider, normalizedGroup), JsonConvert.SerializeObject(list));
            ApplicationOptionSettings.Save();
        }

        public static void Reset(string provider, string groupKey)
        {
            Save(provider, groupKey, BuildDefaults(NormalizeGroup(groupKey)));
        }

        private static List<ViewColumnPreference> BuildDefaults(string groupKey)
        {
            string normalizedGroup = NormalizeGroup(groupKey);
            string[] columns = DefaultColumns[normalizedGroup];
            HashSet<string> visible = DefaultVisibleColumns.ContainsKey(normalizedGroup)
                ? DefaultVisibleColumns[normalizedGroup]
                : Visible(columns);

            return columns.Select(c => new ViewColumnPreference { Name = c, DisplayName = GetColumnDisplayName(c), Visible = visible.Contains(c) }).ToList();
        }

        private static string BuildOptionKey(string provider, string groupKey)
        {
            return "ViewColumns." + NormalizeProvider(provider) + "." + NormalizeGroup(groupKey);
        }

        private static HashSet<string> Visible(params string[] columns)
        {
            return new HashSet<string>(columns ?? new string[0], StringComparer.OrdinalIgnoreCase);
        }
    }
}
