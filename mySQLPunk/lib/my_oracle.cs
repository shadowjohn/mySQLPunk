using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

namespace mySQLPunk.lib
{
    public class my_oracle : IDatabase
    {
        public OracleConnection MCT = null;

        public ConnectionState State => MCT?.State ?? ConnectionState.Closed;
        public string ProviderName => "oracle";

        public void SetConn(string connectionString)
        {
            MCT = new OracleConnection(connectionString);
        }

        public void setConn(string connectionString) => SetConn(connectionString);

        public void Open()
        {
            if (MCT.State != ConnectionState.Open) MCT.Open();
        }

        public void open() => Open();

        public void Close()
        {
            if (MCT != null && MCT.State != ConnectionState.Closed) MCT.Close();
        }

        public void close() => Close();

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            DataTable output = new DataTable();
            using (OracleCommand cmd = new OracleCommand(sql, MCT))
            {
                cmd.BindByName = true;
                AddParameters(cmd, parameters);
                using (OracleDataReader reader = cmd.ExecuteReader())
                {
                    output.Load(reader);
                }
            }
            return output;
        }

        public DataTable selectSQL_SAFE(string sql)
        {
            return SelectSQL(sql);
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            Dictionary<string, string> output = new Dictionary<string, string>();
            try
            {
                using (OracleCommand cmd = new OracleCommand(sql, MCT))
                {
                    cmd.BindByName = true;
                    AddParameters(cmd, parameters);
                    cmd.ExecuteNonQuery();
                }
                output["status"] = "OK";
            }
            catch (Exception ex)
            {
                output["status"] = "NO";
                output["reason"] = ex.Message;
            }
            return output;
        }

        public async Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            DataTable output = new DataTable();
            using (OracleCommand cmd = new OracleCommand(sql, MCT))
            {
                cmd.BindByName = true;
                AddParameters(cmd, parameters);
                using (OracleDataReader reader = await cmd.ExecuteReaderAsync())
                {
                    output.Load(reader);
                }
            }
            return output;
        }

        public async Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            Dictionary<string, string> output = new Dictionary<string, string>();
            try
            {
                using (OracleCommand cmd = new OracleCommand(sql, MCT))
                {
                    cmd.BindByName = true;
                    AddParameters(cmd, parameters);
                    await cmd.ExecuteNonQueryAsync();
                }
                output["status"] = "OK";
            }
            catch (Exception ex)
            {
                output["status"] = "NO";
                output["reason"] = ex.Message;
            }
            return output;
        }

        public List<string> GetDatabases()
        {
            List<string> schemas = new List<string>();
            DataTable current = SelectSQL("SELECT USER AS OWNER FROM DUAL");
            string currentUser = current.Rows.Count > 0 ? current.Rows[0]["OWNER"].ToString() : "";
            if (!string.IsNullOrWhiteSpace(currentUser)) schemas.Add(currentUser);

            DataTable dt = SelectSQL(@"
                SELECT DISTINCT OWNER
                FROM ALL_OBJECTS
                WHERE OBJECT_TYPE IN ('TABLE', 'VIEW')
                  AND OWNER NOT IN ('SYS', 'SYSTEM', 'XDB', 'CTXSYS', 'MDSYS', 'ORDSYS', 'OUTLN', 'DBSNMP')
                ORDER BY OWNER");
            foreach (DataRow row in dt.Rows)
            {
                string owner = row["OWNER"].ToString();
                if (!schemas.Contains(owner, StringComparer.OrdinalIgnoreCase)) schemas.Add(owner);
            }
            return schemas;
        }

        public List<string> GetTables(string databaseName)
        {
            List<string> tables = new List<string>();
            var p = new Dictionary<string, object> { { "owner", NormalizeOwner(databaseName) } };
            DataTable dt = SelectSQL("SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = :owner ORDER BY TABLE_NAME", p);
            foreach (DataRow row in dt.Rows) tables.Add(row["TABLE_NAME"].ToString());
            return tables;
        }

        public List<string> GetViews(string databaseName)
        {
            List<string> views = new List<string>();
            var p = new Dictionary<string, object> { { "owner", NormalizeOwner(databaseName) } };
            DataTable dt = SelectSQL("SELECT VIEW_NAME FROM ALL_VIEWS WHERE OWNER = :owner ORDER BY VIEW_NAME", p);
            foreach (DataRow row in dt.Rows) views.Add(row["VIEW_NAME"].ToString());
            return views;
        }

        public DataTable GetColumns(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object>
            {
                { "owner", NormalizeOwner(databaseName) },
                { "tableName", NormalizeName(tableName) }
            };
            return SelectSQL(@"
                SELECT
                    COLUMN_NAME,
                    DATA_TYPE,
                    NULLABLE AS IS_NULLABLE,
                    DATA_DEFAULT AS COLUMN_DEFAULT,
                    DATA_LENGTH,
                    DATA_PRECISION,
                    DATA_SCALE,
                    COLUMN_ID
                FROM ALL_TAB_COLUMNS
                WHERE OWNER = :owner AND TABLE_NAME = :tableName
                ORDER BY COLUMN_ID", p);
        }

        public DataTable GetIndexes(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object>
            {
                { "owner", NormalizeOwner(databaseName) },
                { "tableName", NormalizeName(tableName) }
            };
            return SelectSQL(@"
                SELECT
                    CASE WHEN c.CONSTRAINT_TYPE = 'P' THEN 'PRIMARY' ELSE i.INDEX_NAME END AS KEY_NAME,
                    ic.COLUMN_NAME,
                    CASE WHEN i.UNIQUENESS = 'UNIQUE' THEN 0 ELSE 1 END AS NON_UNIQUE,
                    ic.COLUMN_POSITION AS SEQ_IN_INDEX,
                    CASE WHEN i.INDEX_TYPE = 'DOMAIN' AND UPPER(i.ITYP_NAME) = 'SPATIAL_INDEX' THEN 'SPATIAL' ELSE i.INDEX_TYPE END AS INDEX_TYPE,
                    '' AS INDEX_COMMENT
                FROM ALL_INDEXES i
                INNER JOIN ALL_IND_COLUMNS ic ON ic.INDEX_OWNER = i.OWNER AND ic.INDEX_NAME = i.INDEX_NAME
                LEFT JOIN ALL_CONSTRAINTS c ON c.OWNER = i.OWNER AND c.INDEX_NAME = i.INDEX_NAME AND c.TABLE_NAME = i.TABLE_NAME
                WHERE i.TABLE_OWNER = :owner AND i.TABLE_NAME = :tableName
                ORDER BY i.INDEX_NAME, ic.COLUMN_POSITION", p);
        }

        public DataTable GetTableStatus(string databaseName)
        {
            var p = new Dictionary<string, object> { { "owner", NormalizeOwner(databaseName) } };
            return SelectSQL(@"
                SELECT
                    TABLE_NAME AS NAME,
                    NULL AS AUTO_INCREMENT,
                    LAST_ANALYZED AS UPDATE_TIME,
                    NULL AS CREATE_TIME,
                    NULL AS CHECK_TIME,
                    BLOCKS * 8192 AS DATA_LENGTH,
                    0 AS INDEX_LENGTH,
                    0 AS MAX_DATA_LENGTH,
                    0 AS DATA_FREE,
                    'Oracle' AS ENGINE,
                    NUM_ROWS AS ROWS,
                    '' AS COMMENTS,
                    '' AS ROW_FORMAT,
                    '' AS COLLATION,
                    '' AS CREATE_OPTIONS
                FROM ALL_TABLES
                WHERE OWNER = :owner
                ORDER BY TABLE_NAME", p);
        }

        public Dictionary<string, string> GetDatabaseInfo(string databaseName)
        {
            Dictionary<string, string> output = new Dictionary<string, string>();
            output["character_set"] = "";
            output["collation"] = "";
            try
            {
                DataTable dt = SelectSQL("SELECT PARAMETER, VALUE FROM NLS_DATABASE_PARAMETERS WHERE PARAMETER IN ('NLS_CHARACTERSET', 'NLS_SORT')");
                foreach (DataRow row in dt.Rows)
                {
                    string key = row["PARAMETER"].ToString();
                    if (key == "NLS_CHARACTERSET") output["character_set"] = row["VALUE"].ToString();
                    if (key == "NLS_SORT") output["collation"] = row["VALUE"].ToString();
                }
            }
            catch { }
            return output;
        }

        public string GetTableCreateStatement(string databaseName, string tableName)
        {
            DataTable columns = GetCopyColumns(databaseName, tableName);
            if (columns.Rows.Count == 0) return "";
            DataTable indexes = TryGetOracleIndexes(databaseName, tableName, true);
            DataTable copyIndexes = TryGetOracleIndexes(databaseName, tableName, false);

            return BuildOracleTableCreateStatement(databaseName, tableName, columns, indexes, copyIndexes);
        }

        private DataTable TryGetOracleIndexes(string databaseName, string tableName, bool includePrimary)
        {
            try
            {
                return includePrimary ? GetIndexes(databaseName, tableName) : GetCopyIndexes(databaseName, tableName);
            }
            catch
            {
                return new DataTable();
            }
        }

        private static string BuildOracleTableCreateStatement(string databaseName, string tableName, DataTable columns, DataTable indexes, DataTable copyIndexes)
        {
            List<string> defs = new List<string>();
            foreach (DataRow row in columns.Rows)
            {
                string nullable = IsCopyNullable(row) ? "NULL" : "NOT NULL";
                string defaultValue = GetDataRowValue(row, "DefaultValue", "DEFAULTVALUE", "ColumnDefault", "COLUMN_DEFAULT");
                string definition = "  " + QuoteIdentifier(GetDataRowValue(row, "Name", "NAME")) + " " + MapCopyTypeToOracle(row);
                if (!string.IsNullOrWhiteSpace(defaultValue))
                {
                    definition += " DEFAULT " + defaultValue.Trim();
                }
                definition += " " + nullable;
                defs.Add(definition);
            }

            List<string> primaryColumns = GetPrimaryKeyColumns(indexes);
            if (primaryColumns.Count > 0)
            {
                defs.Add("  CONSTRAINT " + QuoteIdentifier("PK_" + tableName) +
                         " PRIMARY KEY (" + string.Join(", ", primaryColumns.ToArray()) + ")");
            }

            List<string> statements = new List<string>
            {
                "CREATE TABLE " + QualifiedName(databaseName, tableName) + " (\r\n" +
                string.Join(",\r\n", defs.ToArray()) +
                "\r\n)"
            };

            foreach (DataRow row in columns.Rows)
            {
                string comment = GetDataRowValue(row, "Comment", "COMMENT");
                if (string.IsNullOrWhiteSpace(comment)) continue;
                statements.Add("COMMENT ON COLUMN " + QualifiedName(databaseName, tableName) + "." +
                               QuoteIdentifier(GetDataRowValue(row, "Name", "NAME")) +
                               " IS '" + comment.Replace("'", "''") + "'");
            }

            statements.AddRange(BuildOracleIndexCreateStatements(databaseName, tableName, copyIndexes));
            return string.Join(";\r\n", statements.ToArray());
        }

        public bool TableExists(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object>
            {
                { "owner", NormalizeOwner(databaseName) },
                { "name", NormalizeName(tableName) }
            };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM ALL_TABLES WHERE OWNER = :owner AND TABLE_NAME = :name", p);
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) > 0;
        }

        public bool ViewExists(string databaseName, string viewName)
        {
            var p = new Dictionary<string, object>
            {
                { "owner", NormalizeOwner(databaseName) },
                { "name", NormalizeName(viewName) }
            };
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM ALL_VIEWS WHERE OWNER = :owner AND VIEW_NAME = :name", p);
            return dt.Rows.Count > 0 && Convert.ToInt64(dt.Rows[0][0]) > 0;
        }

        public void RenameTable(string databaseName, string oldTableName, string newTableName)
        {
            ExecOrThrow("ALTER TABLE " + QualifiedName(databaseName, oldTableName) + " RENAME TO " + QuoteIdentifier(newTableName));
        }

        public void RenameView(string databaseName, string oldViewName, string newViewName)
        {
            ExecOrThrow("RENAME " + QualifiedName(databaseName, oldViewName) + " TO " + QuoteIdentifier(newViewName));
        }

        public long CountRows(string databaseName, string tableName)
        {
            DataTable dt = SelectSQL("SELECT COUNT(*) FROM " + QualifiedName(databaseName, tableName));
            return dt.Rows.Count > 0 ? Convert.ToInt64(dt.Rows[0][0]) : 0;
        }

        public DataTable GetCopyColumns(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object>
            {
                { "owner", NormalizeOwner(databaseName) },
                { "tableName", NormalizeName(tableName) }
            };
            return SelectSQL(@"
                SELECT
                    c.COLUMN_NAME AS NAME,
                    c.DATA_TYPE AS DATATYPE,
                    c.NULLABLE AS ISNULLABLE,
                    c.DATA_LENGTH AS MAXLENGTH,
                    c.DATA_PRECISION AS NUMERICPRECISION,
                    c.DATA_SCALE AS NUMERICSCALE,
                    c.DATA_DEFAULT AS DEFAULTVALUE,
                    cc.COMMENTS AS COMMENT,
                    c.COLUMN_ID AS ORDINALPOSITION
                FROM ALL_TAB_COLUMNS c
                LEFT JOIN ALL_COL_COMMENTS cc ON cc.OWNER = c.OWNER AND cc.TABLE_NAME = c.TABLE_NAME AND cc.COLUMN_NAME = c.COLUMN_NAME
                WHERE c.OWNER = :owner AND c.TABLE_NAME = :tableName
                ORDER BY c.COLUMN_ID", p);
        }

        public DataTable GetCopyIndexes(string databaseName, string tableName)
        {
            var p = new Dictionary<string, object>
            {
                { "owner", NormalizeOwner(databaseName) },
                { "tableName", NormalizeName(tableName) }
            };
            return SelectSQL(@"
                SELECT
                    i.INDEX_NAME AS INDEXNAME,
                    ic.COLUMN_NAME AS COLUMNNAME,
                    CASE WHEN i.UNIQUENESS = 'UNIQUE' THEN 0 ELSE 1 END AS NONUNIQUE,
                    ic.COLUMN_POSITION AS SEQININDEX,
                    CASE WHEN i.INDEX_TYPE = 'DOMAIN' AND UPPER(i.ITYP_NAME) = 'SPATIAL_INDEX' THEN 'SPATIAL' ELSE i.INDEX_TYPE END AS INDEXTYPE
                FROM ALL_INDEXES i
                INNER JOIN ALL_IND_COLUMNS ic ON ic.INDEX_OWNER = i.OWNER AND ic.INDEX_NAME = i.INDEX_NAME
                LEFT JOIN ALL_CONSTRAINTS c ON c.OWNER = i.OWNER AND c.INDEX_NAME = i.INDEX_NAME AND c.CONSTRAINT_TYPE = 'P'
                WHERE i.TABLE_OWNER = :owner AND i.TABLE_NAME = :tableName AND c.CONSTRAINT_NAME IS NULL
                ORDER BY i.INDEX_NAME, ic.COLUMN_POSITION", p);
        }

        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider)
        {
            List<string> defs = new List<string>();
            foreach (DataRow row in sourceColumns.Rows)
            {
                string nullable = IsCopyNullable(row) ? "NULL" : "NOT NULL";
                defs.Add(QuoteIdentifier(row["Name"].ToString()) + " " + MapCopyTypeToOracle(row) + " " + nullable);
            }
            ExecOrThrow("CREATE TABLE " + QualifiedName(databaseName, tableName) + " (" + string.Join(", ", defs.ToArray()) + ")");
        }

        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider)
        {
            if (sourceIndexes == null || sourceIndexes.Rows.Count == 0) return;
            foreach (var group in sourceIndexes.AsEnumerable().GroupBy(r => r["IndexName"].ToString()))
            {
                string indexName = group.Key;
                if (string.IsNullOrEmpty(indexName) || indexName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) continue;
                DataRow first = group.First();
                string indexType = first.Table.Columns.Contains("IndexType")
                    ? first["IndexType"].ToString()
                    : (first.Table.Columns.Contains("INDEXTYPE") ? first["INDEXTYPE"].ToString() : "");
                bool unique = first.Table.Columns.Contains("NonUnique") && first["NonUnique"] != DBNull.Value &&
                    (first["NonUnique"].ToString() == "0" || first["NonUnique"].ToString().Equals("False", StringComparison.OrdinalIgnoreCase));
                List<string> cols = new List<string>();
                foreach (DataRow row in group.OrderBy(r => Convert.ToInt32(r["SeqInIndex"])))
                    cols.Add(QuoteIdentifier(row["ColumnName"].ToString()));
                string targetIndexName = tableName + "_" + indexName;
                string sql;
                if (indexType.Equals("SPATIAL", StringComparison.OrdinalIgnoreCase) && cols.Count > 0)
                {
                    sql = "CREATE INDEX " + QuoteIdentifier(targetIndexName) +
                          " ON " + QualifiedName(databaseName, tableName) + " (" + cols[0] + ") INDEXTYPE IS MDSYS.SPATIAL_INDEX";
                }
                else
                {
                    sql = "CREATE " + (unique ? "UNIQUE " : "") + "INDEX " + QuoteIdentifier(targetIndexName) +
                          " ON " + QualifiedName(databaseName, tableName) + " (" + string.Join(",", cols.ToArray()) + ")";
                }
                ExecOrThrow(sql);
            }
        }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            return SelectSQL("SELECT * FROM " + QualifiedName(databaseName, tableName) +
                             " OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY");
        }

        public void InsertTableBatch(string databaseName, string tableName, DataTable rows)
        {
            if (rows == null || rows.Rows.Count == 0) return;
            List<string> cols = new List<string>();
            foreach (DataColumn col in rows.Columns) cols.Add(QuoteIdentifier(col.ColumnName));

            for (int r = 0; r < rows.Rows.Count; r++)
            {
                List<string> vals = new List<string>();
                Dictionary<string, object> p = new Dictionary<string, object>();
                for (int c = 0; c < rows.Columns.Count; c++)
                {
                    string key = "p" + c;
                    vals.Add(":" + key);
                    p[key] = rows.Rows[r][c] == DBNull.Value ? DBNull.Value : rows.Rows[r][c];
                }
                string sql = "INSERT INTO " + QualifiedName(databaseName, tableName) + " (" +
                             string.Join(",", cols.ToArray()) + ") VALUES (" + string.Join(",", vals.ToArray()) + ")";
                ExecOrThrow(sql, p);
            }
        }

        public string GetViewCreateStatement(string databaseName, string viewName)
        {
            var p = new Dictionary<string, object>
            {
                { "owner", NormalizeOwner(databaseName) },
                { "name", NormalizeName(viewName) }
            };
            DataTable dt = SelectSQL("SELECT TEXT FROM ALL_VIEWS WHERE OWNER = :owner AND VIEW_NAME = :name", p);
            if (dt.Rows.Count == 0) return "";
            return "CREATE OR REPLACE VIEW " + QualifiedName(databaseName, viewName) + " AS " + dt.Rows[0]["TEXT"];
        }

        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql)
        {
            string selectSql = ExtractViewSelectSql(sourceViewSql);
            if (string.IsNullOrWhiteSpace(selectSql))
            {
                throw new Exception("無法解析 Oracle View DDL");
            }

            ExecOrThrow("CREATE OR REPLACE VIEW " + QualifiedName(databaseName, viewName) + " AS " + selectSql);
        }

        private void ExecOrThrow(string sql, Dictionary<string, object> parameters = null)
        {
            Dictionary<string, string> res = ExecSQL(sql, parameters);
            if (!res.ContainsKey("status") || res["status"] != "OK")
                throw new Exception(res.ContainsKey("reason") ? res["reason"] : "SQL 執行失敗");
        }

        private static void AddParameters(OracleCommand cmd, Dictionary<string, object> parameters)
        {
            if (parameters == null) return;
            foreach (var key in parameters.Keys)
            {
                cmd.Parameters.Add(new OracleParameter(key, parameters[key] ?? DBNull.Value));
            }
        }

        private static string NormalizeOwner(string name) => NormalizeName(name);
        private static string NormalizeName(string name) => (name ?? "").Trim().ToUpperInvariant();
        private static string QuoteIdentifier(string name) => "\"" + (name ?? "").Replace("\"", "\"\"").ToUpperInvariant() + "\"";
        private static string QualifiedName(string owner, string name) => QuoteIdentifier(owner) + "." + QuoteIdentifier(name);

        private static string ExtractViewSelectSql(string sourceViewSql)
        {
            if (string.IsNullOrWhiteSpace(sourceViewSql)) return "";

            string sql = sourceViewSql.Trim().TrimEnd(';').Trim();
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            {
                return sql;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                sql,
                @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:FORCE\s+|NOFORCE\s+)?VIEW\s+(?:(?:""[^""]+""|\w+)\.)?(?:""[^""]+""|\w+)(?:\s*\([^\)]*\))?\s+AS\s+(?<body>.*)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            return match.Success ? match.Groups["body"].Value.Trim().TrimEnd(';').Trim() : "";
        }

        private static bool IsCopyNullable(DataRow row)
        {
            return !row.Table.Columns.Contains("IsNullable") || row["IsNullable"] == DBNull.Value ||
                   row["IsNullable"].ToString().Equals("Y", StringComparison.OrdinalIgnoreCase) ||
                   row["IsNullable"].ToString().Equals("YES", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> GetPrimaryKeyColumns(DataTable indexes)
        {
            List<string> columns = new List<string>();
            if (indexes == null) return columns;

            foreach (DataRow row in indexes.Rows)
            {
                string keyName = GetDataRowValue(row, "KeyName", "KEY_NAME", "IndexName", "INDEXNAME");
                if (!keyName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) continue;

                string columnName = GetDataRowValue(row, "ColumnName", "COLUMN_NAME", "COLUMNNAME");
                if (!string.IsNullOrWhiteSpace(columnName))
                {
                    columns.Add(QuoteIdentifier(columnName));
                }
            }

            return columns;
        }

        private static List<string> BuildOracleIndexCreateStatements(string databaseName, string tableName, DataTable indexes)
        {
            List<string> statements = new List<string>();
            if (indexes == null || indexes.Rows.Count == 0) return statements;

            foreach (var group in indexes.AsEnumerable().GroupBy(r => GetDataRowValue(r, "IndexName", "INDEXNAME")))
            {
                string indexName = group.Key;
                if (string.IsNullOrWhiteSpace(indexName) || indexName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) continue;
                DataRow first = group.First();
                string indexType = GetDataRowValue(first, "IndexType", "INDEXTYPE");
                bool unique = IsUniqueIndexRow(first);

                List<string> columns = new List<string>();
                foreach (DataRow row in group.OrderBy(r => GetDataRowInt(r, "SeqInIndex", "SEQININDEX")))
                {
                    string columnName = GetDataRowValue(row, "ColumnName", "COLUMNNAME");
                    if (!string.IsNullOrWhiteSpace(columnName))
                    {
                        columns.Add(QuoteIdentifier(columnName));
                    }
                }

                if (columns.Count == 0) continue;

                if (indexType.Equals("SPATIAL", StringComparison.OrdinalIgnoreCase))
                {
                    statements.Add("CREATE INDEX " + QuoteIdentifier(indexName) +
                                   " ON " + QualifiedName(databaseName, tableName) +
                                   " (" + columns[0] + ") INDEXTYPE IS MDSYS.SPATIAL_INDEX");
                    continue;
                }

                statements.Add("CREATE " + (unique ? "UNIQUE " : "") + "INDEX " + QuoteIdentifier(indexName) +
                               " ON " + QualifiedName(databaseName, tableName) +
                               " (" + string.Join(", ", columns.ToArray()) + ")");
            }

            return statements;
        }

        private static bool IsUniqueIndexRow(DataRow row)
        {
            string nonUnique = GetDataRowValue(row, "NonUnique", "NONUNIQUE", "NON_UNIQUE");
            return nonUnique == "0" || nonUnique.Equals("False", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDataRowValue(DataRow row, params string[] names)
        {
            foreach (string name in names)
            {
                if (row.Table.Columns.Contains(name) && row[name] != DBNull.Value)
                {
                    return row[name].ToString();
                }
            }
            return "";
        }

        private static int GetDataRowInt(DataRow row, params string[] names)
        {
            string value = GetDataRowValue(row, names);
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : 0;
        }

        private static string MapCopyTypeToOracle(DataRow row)
        {
            string type = row["DataType"].ToString().ToLowerInvariant();
            if (type.Contains("sdo_geometry") || type == "geometry") return "SDO_GEOMETRY";
            if (type.Contains("bigint") || type.Contains("int") || type.Contains("serial") || type.Contains("number"))
            {
                if (row.Table.Columns.Contains("NumericPrecision") && row["NumericPrecision"] != DBNull.Value &&
                    row.Table.Columns.Contains("NumericScale") && row["NumericScale"] != DBNull.Value)
                    return "NUMBER(" + row["NumericPrecision"] + "," + row["NumericScale"] + ")";
                return "NUMBER";
            }
            if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("money")) return "NUMBER(38,10)";
            if (type.Contains("double") || type.Contains("float") || type.Contains("real")) return "BINARY_DOUBLE";
            if (type.Contains("bool") || type == "bit") return "NUMBER(1)";
            if (type.Contains("date") || type.Contains("time")) return "TIMESTAMP";
            if (type.Contains("blob") || type.Contains("binary") || type.Contains("bytea") || type.Contains("image")) return "BLOB";
            if (type.Contains("clob") || type.Contains("text")) return "CLOB";
            if (type.Contains("char") || type.Contains("varchar"))
            {
                int maxLength = GetDataRowInt(row, "MaxLength", "MAXLENGTH", "DataLength", "DATALENGTH");
                if (maxLength <= 0) maxLength = 255;
                return "VARCHAR2(" + Math.Min(maxLength, 4000) + ")";
            }
            return "CLOB";
        }

        public void Dispose()
        {
            Close();
            MCT?.Dispose();
        }
    }
}
