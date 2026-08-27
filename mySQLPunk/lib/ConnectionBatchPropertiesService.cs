using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace mySQLPunk.lib
{
    public sealed class ConnectionBatchPropertiesChange
    {
        public bool? Starred { get; set; }
        public bool ApplyGroup { get; set; }
        public string Group { get; set; }
        public bool ApplyColor { get; set; }
        public string ColorKey { get; set; }

        public bool HasChanges
        {
            get { return Starred.HasValue || ApplyGroup || ApplyColor; }
        }
    }

    public static class ConnectionBatchPropertiesService
    {
        public const string StarredKey = "conn_starred";
        public const string ColorKey = "conn_color";

        public static readonly string[] SupportedColorKeys =
        {
            "default", "red", "orange", "yellow", "green", "blue", "purple"
        };

        public static bool IsStarred(Dictionary<string, object> connection)
        {
            string value = GetValue(connection, StarredKey).Trim();
            return value == "T" || value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeColorKey(string value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return SupportedColorKeys.Contains(normalized) ? normalized : "default";
        }

        public static string NormalizeGroupPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return string.Join("/", value.Split('/')
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0));
        }

        public static Color GetColor(string colorKey)
        {
            switch (NormalizeColorKey(colorKey))
            {
                case "red": return Color.FromArgb(190, 44, 44);
                case "orange": return Color.FromArgb(204, 120, 50);
                case "yellow": return Color.FromArgb(170, 140, 20);
                case "green": return Color.FromArgb(50, 135, 90);
                case "blue": return Color.FromArgb(51, 103, 145);
                case "purple": return Color.FromArgb(125, 80, 160);
                default: return Color.Empty;
            }
        }

        public static string BuildDisplayName(Dictionary<string, object> connection)
        {
            string name = GetValue(connection, "conn_name");
            return IsStarred(connection) ? "★ " + name : name;
        }

        public static int Apply(
            IList<Dictionary<string, object>> connections,
            IEnumerable<int> selectedIndexes,
            ConnectionBatchPropertiesChange change)
        {
            if (connections == null) throw new ArgumentNullException("connections");
            if (change == null) throw new ArgumentNullException("change");
            if (!change.HasChanges) return 0;

            string group = NormalizeGroupPath(change.Group);
            string color = NormalizeColorKey(change.ColorKey);
            int changed = 0;
            foreach (int index in (selectedIndexes ?? Enumerable.Empty<int>()).Distinct())
            {
                if (index < 0 || index >= connections.Count || connections[index] == null) continue;
                Dictionary<string, object> connection = connections[index];
                bool itemChanged = false;

                if (change.Starred.HasValue)
                {
                    if (IsStarred(connection) != change.Starred.Value)
                    {
                        connection[StarredKey] = change.Starred.Value ? "T" : "F";
                        itemChanged = true;
                    }
                }

                if (change.ApplyGroup && !string.Equals(GetValue(connection, "conn_group"), group, StringComparison.Ordinal))
                {
                    connection["conn_group"] = group;
                    itemChanged = true;
                }

                if (change.ApplyColor && !string.Equals(NormalizeColorKey(GetValue(connection, ColorKey)), color, StringComparison.Ordinal))
                {
                    connection[ColorKey] = color;
                    itemChanged = true;
                }

                if (itemChanged) changed++;
            }
            return changed;
        }

        private static string GetValue(Dictionary<string, object> connection, string key)
        {
            if (connection != null && connection.ContainsKey(key) && connection[key] != null)
                return connection[key].ToString();
            return string.Empty;
        }
    }
}
