using System;
using System.Collections.Generic;

namespace mySQLPunk.lib
{
    public static class DatabaseGroupVisibilityService
    {
        private static readonly HashSet<string> AlwaysVisibleGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Models",
            "BI",
            "Other",
            "Reports",
            "Backups"
        };

        public static bool ShouldShowGroup(string groupKey, int itemCount, bool activeObjectsOnly)
        {
            if (!activeObjectsOnly) return true;
            if (AlwaysVisibleGroups.Contains(groupKey ?? string.Empty)) return true;
            return itemCount > 0;
        }
    }
}
