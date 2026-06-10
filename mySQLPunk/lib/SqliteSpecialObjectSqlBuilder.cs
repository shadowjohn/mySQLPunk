using System;
using System.Collections.Generic;

namespace mySQLPunk.lib
{
    public enum SqliteSpecialObjectKind
    {
        FtsVirtualTable,
        RTreeVirtualTable,
        SpatiaLiteSpatialIndex
    }

    public static class SqliteSpecialObjectSqlBuilder
    {
        public static string BuildFtsVirtualTable(string virtualTableName, IEnumerable<string> columns, string tokenizer, string contentTable)
        {
            string tableName = RequireName(virtualTableName, "virtualTableName");
            List<string> columnList = NormalizeNames(columns, "columns");
            List<string> parts = new List<string>();
            foreach (string columnName in columnList)
            {
                parts.Add(QuoteIdentifier(columnName));
            }

            string cleanTokenizer = string.IsNullOrWhiteSpace(tokenizer) ? "unicode61" : tokenizer.Trim();
            parts.Add("tokenize = " + QuoteString(cleanTokenizer));

            if (!string.IsNullOrWhiteSpace(contentTable))
            {
                parts.Add("content = " + QuoteString(contentTable.Trim()));
            }

            return "CREATE VIRTUAL TABLE " + QuoteIdentifier(tableName) +
                   " USING fts5(" + string.Join(", ", parts.ToArray()) + ");";
        }

        public static string BuildRTreeVirtualTable(string virtualTableName, string idColumn, IEnumerable<string> dimensionColumns)
        {
            string tableName = RequireName(virtualTableName, "virtualTableName");
            string rowId = RequireName(idColumn, "idColumn");
            List<string> dimensions = NormalizeNames(dimensionColumns, "dimensionColumns");
            if (dimensions.Count < 4 || dimensions.Count % 2 != 0)
            {
                throw new ArgumentException(Localization.T("SqliteWizard.RTreeDimensionPairsRequired"));
            }

            List<string> parts = new List<string> { QuoteIdentifier(rowId) };
            foreach (string columnName in dimensions)
            {
                parts.Add(QuoteIdentifier(columnName));
            }

            return "CREATE VIRTUAL TABLE " + QuoteIdentifier(tableName) +
                   " USING rtree(" + string.Join(", ", parts.ToArray()) + ");";
        }

        public static string BuildSpatiaLiteSpatialIndex(string tableName, string geometryColumn)
        {
            string targetTable = RequireName(tableName, "tableName");
            string targetColumn = RequireName(geometryColumn, "geometryColumn");
            return "SELECT CreateSpatialIndex(" + QuoteString(targetTable) + ", " + QuoteString(targetColumn) + ");";
        }

        public static List<string> SplitCommaSeparatedNames(string value)
        {
            List<string> names = new List<string>();
            if (string.IsNullOrWhiteSpace(value)) return names;

            string[] parts = value.Split(',');
            foreach (string part in parts)
            {
                string name = part.Trim();
                if (name.Length > 0) names.Add(name);
            }
            return names;
        }

        private static List<string> NormalizeNames(IEnumerable<string> names, string parameterName)
        {
            List<string> output = new List<string>();
            if (names != null)
            {
                foreach (string name in names)
                {
                    string normalized = (name ?? "").Trim();
                    if (normalized.Length > 0) output.Add(normalized);
                }
            }

            if (output.Count == 0) throw new ArgumentException(Localization.Format("SqliteWizard.ValueRequired", parameterName));
            return output;
        }

        private static string RequireName(string value, string parameterName)
        {
            string name = (value ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException(Localization.Format("SqliteWizard.ValueRequired", parameterName));
            return name;
        }

        private static string QuoteIdentifier(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        }

        private static string QuoteString(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }
    }
}
