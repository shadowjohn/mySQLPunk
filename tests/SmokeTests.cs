using System;
using System.Data;
using System.Reflection;
using System.Windows.Forms;
using mySQLPunk;
using mySQLPunk.lib;

public static class SmokeTests
{
    [STAThread]
    public static int Main()
    {
        int passed = 0;
        Run("Geometry WKB 轉 WKT", TestGeometryWktConverter, ref passed);
        Run("View SQL 跨 provider 轉換", TestViewSqlConversion, ref passed);
        Run("View SQL 進階轉換案例", TestAdvancedViewSqlConversion, ref passed);
        Run("Table Designer DDL builder", TestTableDesignerDdlBuilder, ref passed);
        Console.WriteLine("Smoke tests passed: " + passed);
        return 0;
    }

    private static void Run(string name, Action test, ref int passed)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine("[PASS] " + name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + name);
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
        }
    }

    private static void TestGeometryWktConverter()
    {
        byte[] wkb = BuildLittleEndianPointWkb(121.5, 25);
        string wkt;
        Assert(GeometryWktConverter.TryGeometryBytesToWkt(wkb, out wkt), "WKB point should convert to WKT.");
        AssertEquals("POINT (121.5 25)", wkt, "Unexpected WKT point output.");

        Assert(!GeometryWktConverter.TryGeometryBytesToWkt(new byte[] { 1, 2, 3, 4 }, out wkt), "Invalid geometry bytes should fail cleanly.");
    }

    private static byte[] BuildLittleEndianPointWkb(double x, double y)
    {
        byte[] bytes = new byte[21];
        bytes[0] = 1;
        Array.Copy(BitConverter.GetBytes((uint)1), 0, bytes, 1, 4);
        Array.Copy(BitConverter.GetBytes(x), 0, bytes, 5, 8);
        Array.Copy(BitConverter.GetBytes(y), 0, bytes, 13, 8);
        return bytes;
    }

    private static void TestViewSqlConversion()
    {
        object mysqlPreview = BuildViewSqlPreview(
            "CREATE VIEW v AS SELECT TOP (5) Id, GETDATE() AS CheckedAt FROM dbo.Users",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlPreview, "CanConvert"), "SQL Server TOP query should convert to MySQL.");
        string mysqlSql = (string)GetProperty(mysqlPreview, "ConvertedSql");
        AssertContains(mysqlSql, "LIMIT 5", "Converted MySQL SQL should contain LIMIT.");
        AssertContains(mysqlSql, "CURRENT_TIMESTAMP", "Converted MySQL SQL should rewrite GETDATE().");

        object mssqlPreview = BuildViewSqlPreview(
            "SELECT id, DATE_FORMAT(created_at, '%Y-%m-%d') AS day_text FROM logs LIMIT 10",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlPreview, "CanConvert"), "MySQL LIMIT query should convert to SQL Server.");
        string mssqlSql = (string)GetProperty(mssqlPreview, "ConvertedSql");
        AssertContains(mssqlSql, "TOP (10)", "Converted SQL Server SQL should contain TOP.");
        AssertContains(mssqlSql, "FORMAT(", "Converted SQL Server SQL should rewrite DATE_FORMAT.");
    }

    private static void TestAdvancedViewSqlConversion()
    {
        object pgPreview = BuildViewSqlPreview(
            "SELECT id, name FROM accounts WHERE is_active = 1 AND ROWNUM <= 3",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgPreview, "CanConvert"), "Simple Oracle ROWNUM predicate should convert to PostgreSQL.");
        string pgSql = (string)GetProperty(pgPreview, "ConvertedSql");
        AssertContains(pgSql, "LIMIT 3", "Converted PostgreSQL SQL should contain LIMIT from ROWNUM.");
        AssertNotContains(pgSql, "ROWNUM", "Converted PostgreSQL SQL should remove simple ROWNUM predicate.");

        object aggregatePreview = BuildViewSqlPreview(
            "SELECT project_id, GROUP_CONCAT(user_name SEPARATOR '|') AS members FROM project_users GROUP BY project_id",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(aggregatePreview, "CanConvert"), "MySQL GROUP_CONCAT should convert to SQL Server.");
        string aggregateSql = (string)GetProperty(aggregatePreview, "ConvertedSql");
        AssertContains(aggregateSql, "STRING_AGG(user_name, '|')", "Converted SQL Server SQL should use STRING_AGG.");

        object jsonPreview = BuildViewSqlPreview(
            "SELECT JSON_EXTRACT(payload, '$.status') AS status_text FROM event_log",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(jsonPreview, "CanConvert"), "MySQL JSON_EXTRACT should convert to Oracle.");
        string jsonSql = (string)GetProperty(jsonPreview, "ConvertedSql");
        AssertContains(jsonSql, "JSON_VALUE(payload, '$.status')", "Converted Oracle SQL should use JSON_VALUE.");

        object cteWindowPreview = BuildViewSqlPreview(
            "WITH ranked AS (SELECT id, ROW_NUMBER() OVER (PARTITION BY group_id ORDER BY created_at DESC) AS rn FROM items) SELECT id FROM ranked WHERE rn = 1",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(cteWindowPreview, "CanConvert"), "Portable CTE/window SQL should be preserved for SQL Server.");
        string cteWindowSql = (string)GetProperty(cteWindowPreview, "ConvertedSql");
        AssertContains(cteWindowSql, "WITH ranked AS", "Converted SQL should preserve CTE.");
        AssertContains(cteWindowSql, "ROW_NUMBER() OVER", "Converted SQL should preserve window function.");

        object unsupportedPreview = BuildViewSqlPreview(
            "CREATE VIEW v AS SELECT id FROM employee START WITH manager_id IS NULL CONNECT BY PRIOR id = manager_id",
            "oracle",
            "mysql");
        Assert(!(bool)GetProperty(unsupportedPreview, "CanConvert"), "Oracle hierarchical query should be rejected instead of converted silently.");
        string reason = (string)GetProperty(unsupportedPreview, "Reason");
        Assert(!string.IsNullOrWhiteSpace(reason), "Unsupported conversion should return a readable reason.");
    }

    private static object BuildViewSqlPreview(string sql, string sourceProvider, string targetProvider)
    {
        Type converter = typeof(DatabaseCopyService).Assembly.GetType("mySQLPunk.lib.ViewSqlDialectConverter", true);
        MethodInfo method = converter.GetMethod("BuildPreview", BindingFlags.Public | BindingFlags.Static);
        return method.Invoke(null, new object[] { sql, sourceProvider, targetProvider });
    }

    private static object GetProperty(object target, string name)
    {
        return target.GetType().GetProperty(name).GetValue(target, null);
    }

    private static void TestTableDesignerDdlBuilder()
    {
        string mysqlSql = BuildCreateTableSql(new my_mysql(), "codex_smoke_mysql");
        AssertContains(mysqlSql, "CREATE TABLE", "MySQL create table SQL should be generated.");
        AssertContains(mysqlSql, "codex_smoke_mysql", "MySQL create table SQL should include table name.");
        AssertContains(mysqlSql, "PRIMARY KEY", "MySQL create table SQL should include primary key.");

        string sqliteSql = BuildCreateTableSql(new my_sqlite(), "codex_smoke_sqlite");
        AssertContains(sqliteSql, "CREATE TABLE", "SQLite create table SQL should be generated.");
        AssertContains(sqliteSql, "codex_smoke_sqlite", "SQLite create table SQL should include table name.");
        AssertContains(sqliteSql, "__mysqlpunk_column_comments", "SQLite create table SQL should include sidecar comments when comments exist.");
    }

    private static string BuildCreateTableSql(IDatabase db, string tableName)
    {
        using (TableDesignerForm form = new TableDesignerForm(db, "main", null))
        {
            SetTextBoxField(form, "txtTableName", tableName);

            MethodInfo createTableMethod = typeof(TableDesignerForm).GetMethod("CreateColumnsDisplayTable", BindingFlags.Instance | BindingFlags.NonPublic);
            DataTable columns = (DataTable)createTableMethod.Invoke(form, null);

            DataRow row = columns.NewRow();
            row["Name"] = "Id";
            row["Type"] = "int";
            row["NotNull"] = true;
            row["PK"] = true;
            row["Comment"] = "識別碼";
            columns.Rows.Add(row);

            MethodInfo buildMethod = typeof(TableDesignerForm).GetMethod("BuildCreateTableSql", BindingFlags.Instance | BindingFlags.NonPublic);
            return (string)buildMethod.Invoke(form, new object[] { columns });
        }
    }

    private static void SetTextBoxField(object target, string fieldName, string value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        TextBox textBox = (TextBox)field.GetValue(target);
        textBox.Text = value;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void AssertEquals(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new Exception(message + " Expected: " + expected + " Actual: " + actual);
        }
    }

    private static void AssertContains(string value, string expected, string message)
    {
        if (value == null || value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new Exception(message + " Expected fragment: " + expected + " Actual: " + value);
        }
    }

    private static void AssertNotContains(string value, string unexpected, string message)
    {
        if (value != null && value.IndexOf(unexpected, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new Exception(message + " Unexpected fragment: " + unexpected + " Actual: " + value);
        }
    }
}
