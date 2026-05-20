using System;
using System.Collections.Generic;
using System.Linq;

namespace mySQLPunk.lib
{
    public static class ObjectVisibilityService
    {
        private static readonly HashSet<string> HiddenSqliteObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "__mysqlpunk_column_comments",
            "geometry_columns",
            "geometry_columns_auth",
            "geometry_columns_field_infos",
            "geometry_columns_statistics",
            "geometry_columns_time",
            "spatial_ref_sys",
            "spatial_ref_sys_aux",
            "spatial_ref_sys_all",
            "sqlite_sequence",
            "sqlite_stat1",
            "sqlite_stat2",
            "sqlite_stat3",
            "sqlite_stat4",
            "sql_statements_log",
            "virts_geometry_columns",
            "views_geometry_columns",
            "views_geometry_columns_auth",
            "views_geometry_columns_field_infos",
            "views_geometry_columns_statistics"
        };

        public static List<string> FilterNames(IEnumerable<string> names, string providerName, string objectKind, bool includeHidden)
        {
            if (names == null) return new List<string>();

            return names
                .Where(name => IsVisibleName(providerName, objectKind, name, includeHidden))
                .ToList();
        }

        public static bool IsVisibleName(string providerName, string objectKind, string objectName, bool includeHidden)
        {
            if (includeHidden) return true;
            string name = (objectName ?? string.Empty).Trim();
            if (name.Length == 0) return false;

            string provider = NormalizeProvider(providerName);
            string kind = (objectKind ?? string.Empty).Trim().ToLowerInvariant();

            if (provider == "sqlite")
            {
                if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) return false;
                if (name.StartsWith("idx_", StringComparison.OrdinalIgnoreCase) && name.IndexOf("_geometry_", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (name.EndsWith("_geometry_node", StringComparison.OrdinalIgnoreCase)) return false;
                if (name.EndsWith("_geometry_parent", StringComparison.OrdinalIgnoreCase)) return false;
                if (name.EndsWith("_geometry_rowid", StringComparison.OrdinalIgnoreCase)) return false;
                if (HiddenSqliteObjects.Contains(name)) return false;
                if (kind == "view" && name.StartsWith("views_geometry_", StringComparison.OrdinalIgnoreCase)) return false;
            }

            if (provider == "mysql" || provider == "mariadb")
            {
                if (name.StartsWith("INNODB_", StringComparison.OrdinalIgnoreCase)) return false;
                if (name.StartsWith("sys_", StringComparison.OrdinalIgnoreCase)) return false;
            }

            if (provider == "postgresql")
            {
                if (name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase)) return false;
                if (name.StartsWith("information_schema.", StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }

        private static string NormalizeProvider(string providerName)
        {
            string provider = (providerName ?? string.Empty).Trim().ToLowerInvariant();
            if (provider == "sqlserver" || provider == "sql server") return "mssql";
            if (provider == "postgres" || provider == "pgsql") return "postgresql";
            return provider;
        }
    }
}
