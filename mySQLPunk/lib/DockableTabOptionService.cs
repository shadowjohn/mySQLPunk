using System;

namespace mySQLPunk.lib
{
    public static class DockableTabOptionService
    {
        public static bool ResolveDockPreference(bool requestedDocked, string optionValue, bool hasDockedTabs)
        {
            string normalized = (optionValue ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "new") return false;
            if (normalized == "main") return true;
            if (normalized == "last") return hasDockedTabs || requestedDocked;
            return requestedDocked;
        }

        public static bool ShouldReuseTab(bool allowDuplicateObjects, string existingTitle, string newTitle, Type existingType, Type newType)
        {
            if (allowDuplicateObjects) return false;
            if (existingType == null || newType == null || existingType != newType) return false;
            return string.Equals(
                NormalizeTitle(existingTitle),
                NormalizeTitle(newTitle),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeTitle(string value)
        {
            return (value ?? string.Empty).Trim();
        }
    }
}
