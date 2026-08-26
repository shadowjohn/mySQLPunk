using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace mySQLPunk.lib
{
    public sealed class SchemaModelSnapshot
    {
        public SchemaModelSnapshot()
        {
            Tables = new List<SchemaTableModel>();
            Relationships = new List<SchemaRelationshipModel>();
            Warnings = new List<string>();
        }

        public string DatabaseName { get; set; }
        public string ProviderName { get; set; }
        public List<SchemaTableModel> Tables { get; private set; }
        public List<SchemaRelationshipModel> Relationships { get; private set; }
        public List<string> Warnings { get; private set; }
    }

    public sealed class SchemaTableModel
    {
        public SchemaTableModel()
        {
            Columns = new List<SchemaColumnModel>();
        }

        public string Name { get; set; }
        public List<SchemaColumnModel> Columns { get; private set; }
    }

    public sealed class SchemaColumnModel
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool IsNullable { get; set; }
        public bool IsPrimaryKey { get; set; }
        public int Ordinal { get; set; }
    }

    public sealed class SchemaRelationshipModel
    {
        public string Name { get; set; }
        public string FromTable { get; set; }
        public string FromColumn { get; set; }
        public string ToTable { get; set; }
        public string ToColumn { get; set; }
        public int Ordinal { get; set; }
    }

    /// <summary>
    /// 建立 provider-neutral 的資料庫結構快照。第一個使用者是唯讀 ER 圖，
    /// 後續結構比較與同步預覽也共用同一份模型，避免各功能各查一次 metadata。
    /// </summary>
    public static class SchemaModelService
    {
        public static SchemaModelSnapshot Load(IDatabase database, string databaseName)
        {
            if (database == null) throw new ArgumentNullException("database");
            if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException("Database name is required.", "databaseName");

            SchemaModelSnapshot snapshot = new SchemaModelSnapshot
            {
                DatabaseName = databaseName.Trim(),
                ProviderName = NormalizeProvider(database.ProviderName)
            };

            List<string> tableNames = (database.GetTables(databaseName) ?? new List<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string tableName in tableNames)
            {
                SchemaTableModel table = new SchemaTableModel { Name = tableName };
                try
                {
                    LoadColumns(database, databaseName, table);
                }
                catch (Exception ex)
                {
                    snapshot.Warnings.Add(tableName + ": " + ex.Message);
                }
                snapshot.Tables.Add(table);
            }

            try
            {
                snapshot.Relationships.AddRange(LoadRelationships(database, databaseName, snapshot.ProviderName, tableNames));
            }
            catch (Exception ex)
            {
                snapshot.Warnings.Add("Foreign keys: " + ex.Message);
            }

            return snapshot;
        }

        private static void LoadColumns(IDatabase database, string databaseName, SchemaTableModel table)
        {
            DataTable columns = database.GetColumns(databaseName, table.Name) ?? new DataTable();
            int fallbackOrdinal = 0;
            foreach (DataRow row in columns.Rows)
            {
                fallbackOrdinal++;
                string name = ReadString(row, "Field", "COLUMN_NAME", "column_name", "Name", "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                string nullable = ReadString(row, "Null", "IS_NULLABLE", "is_nullable", "NULLABLE");
                string notNull = ReadString(row, "notnull");
                string key = ReadString(row, "Key", "COLUMN_KEY", "ColumnKey");
                int sqlitePk = ReadInt(row, "pk");
                int ordinal = ReadInt(row, "ORDINAL_POSITION", "ordinal_position", "OrdinalPosition", "COLUMN_ID");
                if (ordinal <= 0)
                {
                    int sqliteCid;
                    if (int.TryParse(ReadString(row, "cid"), out sqliteCid) && sqliteCid >= 0) ordinal = sqliteCid + 1;
                }

                table.Columns.Add(new SchemaColumnModel
                {
                    Name = name,
                    DataType = ReadString(row, "Type", "DATA_TYPE", "data_type", "type"),
                    IsNullable = ResolveNullable(nullable, notNull),
                    IsPrimaryKey = string.Equals(key, "PRI", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(key, "PRIMARY", StringComparison.OrdinalIgnoreCase) ||
                                   sqlitePk > 0,
                    Ordinal = FirstPositive(ordinal, fallbackOrdinal)
                });
            }

            MarkPrimaryKeyColumns(database, databaseName, table);
            table.Columns.Sort((left, right) =>
            {
                int order = left.Ordinal.CompareTo(right.Ordinal);
                return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            });
        }

        private static void MarkPrimaryKeyColumns(IDatabase database, string databaseName, SchemaTableModel table)
        {
            DataTable indexes;
            try { indexes = database.GetIndexes(databaseName, table.Name); }
            catch { return; }
            if (indexes == null) return;

            foreach (DataRow row in indexes.Rows)
            {
                string indexName = ReadString(row, "Key_name", "KEY_NAME", "IndexName", "INDEX_NAME");
                if (!string.Equals(indexName, "PRIMARY", StringComparison.OrdinalIgnoreCase)) continue;

                string columnName = ReadString(row, "Column_name", "COLUMN_NAME", "ColumnName");
                SchemaColumnModel column = table.Columns.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, columnName, StringComparison.OrdinalIgnoreCase));
                if (column != null) column.IsPrimaryKey = true;
            }
        }

        private static List<SchemaRelationshipModel> LoadRelationships(
            IDatabase database,
            string databaseName,
            string provider,
            List<string> tableNames)
        {
            DataTable rows;
            if (provider == "sqlite")
            {
                return LoadSqliteRelationships(database, tableNames);
            }
            if (provider == "mysql")
            {
                rows = database.SelectSQL(@"
                    SELECT
                        CONSTRAINT_NAME AS RelationshipName,
                        TABLE_NAME AS FromTable,
                        COLUMN_NAME AS FromColumn,
                        REFERENCED_TABLE_NAME AS ToTable,
                        REFERENCED_COLUMN_NAME AS ToColumn,
                        ORDINAL_POSITION AS ColumnOrdinal
                    FROM information_schema.KEY_COLUMN_USAGE
                    WHERE TABLE_SCHEMA = ?databaseName
                      AND REFERENCED_TABLE_NAME IS NOT NULL
                    ORDER BY TABLE_NAME, CONSTRAINT_NAME, ORDINAL_POSITION;",
                    new Dictionary<string, object> { { "databaseName", databaseName } });
            }
            else if (provider == "postgresql")
            {
                rows = database.SelectSQL(@"
                    SELECT
                        con.conname AS ""RelationshipName"",
                        CASE WHEN child_ns.nspname = 'public' THEN child.relname ELSE child_ns.nspname || '.' || child.relname END AS ""FromTable"",
                        child_col.attname AS ""FromColumn"",
                        CASE WHEN parent_ns.nspname = 'public' THEN parent.relname ELSE parent_ns.nspname || '.' || parent.relname END AS ""ToTable"",
                        parent_col.attname AS ""ToColumn"",
                        child_key.ordinality AS ""ColumnOrdinal""
                    FROM pg_constraint con
                    INNER JOIN pg_class child ON child.oid = con.conrelid
                    INNER JOIN pg_namespace child_ns ON child_ns.oid = child.relnamespace
                    INNER JOIN pg_class parent ON parent.oid = con.confrelid
                    INNER JOIN pg_namespace parent_ns ON parent_ns.oid = parent.relnamespace
                    INNER JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS child_key(attnum, ordinality) ON TRUE
                    INNER JOIN pg_attribute child_col ON child_col.attrelid = child.oid AND child_col.attnum = child_key.attnum
                    INNER JOIN LATERAL unnest(con.confkey) WITH ORDINALITY AS parent_key(attnum, ordinality) ON parent_key.ordinality = child_key.ordinality
                    INNER JOIN pg_attribute parent_col ON parent_col.attrelid = parent.oid AND parent_col.attnum = parent_key.attnum
                    WHERE con.contype = 'f'
                    ORDER BY child_ns.nspname, child.relname, con.conname, child_key.ordinality;");
            }
            else if (provider == "mssql")
            {
                string databaseIdentifier = "[" + (databaseName ?? string.Empty).Replace("]", "]]" ) + "]";
                rows = database.SelectSQL(@"
                    SELECT
                        fk.name AS [RelationshipName],
                        CASE WHEN child_schema.name = 'dbo' THEN child_table.name ELSE child_schema.name + '.' + child_table.name END AS [FromTable],
                        child_column.name AS [FromColumn],
                        CASE WHEN parent_schema.name = 'dbo' THEN parent_table.name ELSE parent_schema.name + '.' + parent_table.name END AS [ToTable],
                        parent_column.name AS [ToColumn],
                        fkc.constraint_column_id AS [ColumnOrdinal]
                    FROM " + databaseIdentifier + @".sys.foreign_keys fk
                    INNER JOIN " + databaseIdentifier + @".sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
                    INNER JOIN " + databaseIdentifier + @".sys.tables child_table ON child_table.object_id = fkc.parent_object_id
                    INNER JOIN " + databaseIdentifier + @".sys.schemas child_schema ON child_schema.schema_id = child_table.schema_id
                    INNER JOIN " + databaseIdentifier + @".sys.columns child_column ON child_column.object_id = child_table.object_id AND child_column.column_id = fkc.parent_column_id
                    INNER JOIN " + databaseIdentifier + @".sys.tables parent_table ON parent_table.object_id = fkc.referenced_object_id
                    INNER JOIN " + databaseIdentifier + @".sys.schemas parent_schema ON parent_schema.schema_id = parent_table.schema_id
                    INNER JOIN " + databaseIdentifier + @".sys.columns parent_column ON parent_column.object_id = parent_table.object_id AND parent_column.column_id = fkc.referenced_column_id
                    ORDER BY child_schema.name, child_table.name, fk.name, fkc.constraint_column_id;");
            }
            else if (provider == "oracle")
            {
                rows = database.SelectSQL(@"
                    SELECT
                        fk.CONSTRAINT_NAME AS ""RelationshipName"",
                        CASE WHEN fk.OWNER = :owner THEN fk.TABLE_NAME ELSE fk.OWNER || '.' || fk.TABLE_NAME END AS ""FromTable"",
                        fkc.COLUMN_NAME AS ""FromColumn"",
                        CASE WHEN pk.OWNER = :owner THEN pk.TABLE_NAME ELSE pk.OWNER || '.' || pk.TABLE_NAME END AS ""ToTable"",
                        pkc.COLUMN_NAME AS ""ToColumn"",
                        fkc.POSITION AS ""ColumnOrdinal""
                    FROM ALL_CONSTRAINTS fk
                    INNER JOIN ALL_CONS_COLUMNS fkc ON fkc.OWNER = fk.OWNER AND fkc.CONSTRAINT_NAME = fk.CONSTRAINT_NAME
                    INNER JOIN ALL_CONSTRAINTS pk ON pk.OWNER = fk.R_OWNER AND pk.CONSTRAINT_NAME = fk.R_CONSTRAINT_NAME
                    INNER JOIN ALL_CONS_COLUMNS pkc ON pkc.OWNER = pk.OWNER AND pkc.CONSTRAINT_NAME = pk.CONSTRAINT_NAME AND pkc.POSITION = fkc.POSITION
                    WHERE fk.CONSTRAINT_TYPE = 'R' AND fk.OWNER = :owner
                    ORDER BY fk.TABLE_NAME, fk.CONSTRAINT_NAME, fkc.POSITION",
                    new Dictionary<string, object> { { "owner", (databaseName ?? string.Empty).ToUpperInvariant() } });
            }
            else
            {
                return new List<SchemaRelationshipModel>();
            }

            return ParseRelationships(rows, tableNames);
        }

        private static List<SchemaRelationshipModel> LoadSqliteRelationships(IDatabase database, IEnumerable<string> tableNames)
        {
            List<SchemaRelationshipModel> output = new List<SchemaRelationshipModel>();
            foreach (string tableName in tableNames)
            {
                string escapedTable = (tableName ?? string.Empty).Replace("'", "''");
                DataTable rows = database.SelectSQL("PRAGMA foreign_key_list('" + escapedTable + "');");
                if (rows == null) continue;

                foreach (DataRow row in rows.Rows)
                {
                    string id = ReadString(row, "id");
                    int ordinal = ReadInt(row, "seq") + 1;
                    output.Add(new SchemaRelationshipModel
                    {
                        Name = "FK_" + tableName + "_" + id,
                        FromTable = tableName,
                        FromColumn = ReadString(row, "from"),
                        ToTable = ReadString(row, "table"),
                        ToColumn = ReadString(row, "to"),
                        Ordinal = FirstPositive(ordinal, 1)
                    });
                }
            }
            return FilterAndSortRelationships(output, tableNames);
        }

        private static List<SchemaRelationshipModel> ParseRelationships(DataTable rows, IEnumerable<string> tableNames)
        {
            List<SchemaRelationshipModel> output = new List<SchemaRelationshipModel>();
            if (rows == null) return output;

            foreach (DataRow row in rows.Rows)
            {
                output.Add(new SchemaRelationshipModel
                {
                    Name = ReadString(row, "RelationshipName", "RELATIONSHIPNAME", "relationshipname"),
                    FromTable = ReadString(row, "FromTable", "FROMTABLE", "fromtable"),
                    FromColumn = ReadString(row, "FromColumn", "FROMCOLUMN", "fromcolumn"),
                    ToTable = ReadString(row, "ToTable", "TOTABLE", "totable"),
                    ToColumn = ReadString(row, "ToColumn", "TOCOLUMN", "tocolumn"),
                    Ordinal = FirstPositive(ReadInt(row, "ColumnOrdinal", "COLUMNORDINAL", "columnordinal"), 1)
                });
            }
            return FilterAndSortRelationships(output, tableNames);
        }

        private static List<SchemaRelationshipModel> FilterAndSortRelationships(
            IEnumerable<SchemaRelationshipModel> relationships,
            IEnumerable<string> tableNames)
        {
            HashSet<string> knownTables = new HashSet<string>(tableNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return (relationships ?? Enumerable.Empty<SchemaRelationshipModel>())
                .Where(item => item != null &&
                               !string.IsNullOrWhiteSpace(item.FromTable) &&
                               !string.IsNullOrWhiteSpace(item.FromColumn) &&
                               !string.IsNullOrWhiteSpace(item.ToTable) &&
                               !string.IsNullOrWhiteSpace(item.ToColumn) &&
                               knownTables.Contains(item.FromTable) &&
                               knownTables.Contains(item.ToTable))
                .OrderBy(item => item.FromTable, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Ordinal)
                .ToList();
        }

        private static bool ResolveNullable(string nullable, string notNull)
        {
            if (!string.IsNullOrWhiteSpace(nullable))
            {
                return string.Equals(nullable, "YES", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(nullable, "Y", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(nullable, "TRUE", StringComparison.OrdinalIgnoreCase);
            }
            if (!string.IsNullOrWhiteSpace(notNull)) return notNull == "0";
            return true;
        }

        private static int FirstPositive(int value, int fallback)
        {
            return value > 0 ? value : fallback;
        }

        private static string NormalizeProvider(string provider)
        {
            string value = (provider ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "sqlserver" || value == "sql server") return "mssql";
            if (value == "postgres" || value == "npgsql") return "postgresql";
            return value;
        }

        private static int ReadInt(DataRow row, params string[] names)
        {
            string value = ReadString(row, names);
            int result;
            return int.TryParse(value, out result) ? result : 0;
        }

        private static string ReadString(DataRow row, params string[] names)
        {
            if (row == null || row.Table == null) return string.Empty;
            foreach (string name in names)
            {
                foreach (DataColumn column in row.Table.Columns)
                {
                    if (!string.Equals(column.ColumnName, name, StringComparison.OrdinalIgnoreCase)) continue;
                    object value = row[column];
                    return value == null || value == DBNull.Value ? string.Empty : value.ToString().Trim();
                }
            }
            return string.Empty;
        }
    }
}
