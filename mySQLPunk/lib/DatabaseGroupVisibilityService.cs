using System;
using System.Collections.Generic;

namespace mySQLPunk.lib
{
    public static class DatabaseGroupVisibilityService
    {
        private static readonly HashSet<string> ObjectGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Tables",
            "Views",
            "Functions",
            "Users",
            "Events",
            "Queries"
        };

        private static readonly HashSet<string> ActionGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Models",
            "BI",
            "Other",
            "Reports",
            "Backups"
        };

        public static bool IsKnownGroup(string groupKey)
        {
            string key = groupKey ?? string.Empty;
            return ObjectGroups.Contains(key) || ActionGroups.Contains(key);
        }

        public static bool IsObjectGroup(string groupKey)
        {
            return ObjectGroups.Contains(groupKey ?? string.Empty);
        }

        public static bool IsActionGroup(string groupKey)
        {
            return ActionGroups.Contains(groupKey ?? string.Empty);
        }

        public static bool ShouldShowGroup(string groupKey, int itemCount, bool activeObjectsOnly)
        {
            if (!activeObjectsOnly) return true;
            if (IsActionGroup(groupKey)) return true;
            return itemCount > 0;
        }

        public static bool ShouldFlattenGroup(string groupKey, bool hideObjectGroups)
        {
            return hideObjectGroups && IsObjectGroup(groupKey);
        }
    }
}
