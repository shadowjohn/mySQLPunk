using System;
using System.Data;
using System.Linq;
using mySQLPunk.lib;

public static class SqlServerMigrationCatalogTests
{
    public static void Run()
    {
        foreach (string endpoint in new[] { "source", "target" })
        foreach (bool disabled in new[] { false, true })
        {
            bool queried = false;
            var catalog = SqlServerMigrationCatalogReader.Read(sql => {
                if (sql.Contains("sys.triggers"))
                {
                    Check(sql.Contains("parent_class=0") && !sql.Contains("is_disabled=0"), "Database scope includes disabled triggers");
                    queried = true;
                    var triggers = new DataTable(); triggers.Columns.Add("name"); triggers.Columns.Add("is_disabled", typeof(bool));
                    triggers.Rows.Add("fixture_ddl_" + endpoint, disabled); return triggers;
                }
                return Fixture(sql, endpoint);
            });
            Check(queried && catalog.Blockers.Any(reason => reason.Contains("fixture_ddl_" + endpoint)), "Real catalog query blocks " + endpoint + " DDL trigger");
            var source = SqlServerMigrationCatalogReader.Read(sql => Fixture(sql, "source"));
            var target = SqlServerMigrationCatalogReader.Read(sql => Fixture(sql, "target"));
            if (endpoint == "source") source = catalog; else target = catalog;
            var preview = SqlServerCompatibilityMigration.Preview(source, target, 15);
            Check(!preview.CanStart && preview.Blockers.Any(reason => reason.Contains("fixture_ddl_" + endpoint)), "Preview preserves endpoint trigger blocker");
        }
        foreach (string endpoint in new[] { "source", "target" })
        {
            bool failureClosed = false;
            try
            {
                SqlServerMigrationCatalogReader.Read(sql => {
                    if (sql.Contains("sys.triggers")) throw new InvalidOperationException("synthetic metadata denied");
                    return Fixture(sql, endpoint);
                });
            }
            catch (InvalidOperationException) { failureClosed = true; }
            Check(failureClosed, "Unreadable " + endpoint + " trigger catalog never produces an executable preview");
        }
        var empty = SqlServerMigrationCatalogReader.Read(sql => Fixture(sql, "target"));
        Check(empty.Blockers.Count == 0, "Ordinary empty target without DDL triggers remains usable");
        var ordinary = SqlServerMigrationCatalogReader.Read(sql => Fixture(sql, "source"));
        Check(SqlServerCompatibilityMigration.Preview(ordinary, empty, 15).CanStart, "Same catalog fixture passes without database triggers");
    }

    private static DataTable Fixture(string sql, string endpoint)
    {
        var result = new DataTable();
        if (sql.Contains("SERVERPROPERTY"))
        {
            foreach (string column in new[] { "server_name", "version", "db_name", "db_id", "create_date", "collation_name" }) result.Columns.Add(column);
            result.Rows.Add("isolated", endpoint == "source" ? "17.0.1.0" : "15.0.1.0", endpoint, endpoint == "source" ? "5" : "6", "2026-01-01", "same");
        }
        else if (sql.Contains("HAS_PERMS_BY_NAME"))
        {
            result.Columns.Add("visible"); result.Columns.Add("controlled"); result.Rows.Add("1", "1");
        }
        else if (endpoint == "source" && sql.Contains("FROM sys.objects"))
        {
            result.Columns.Add("type"); result.Columns.Add("name"); result.Rows.Add("U", "fixture_rows");
        }
        else if (endpoint == "source" && sql.Contains("FROM sys.tables t"))
        {
            result.Columns.Add("object_id", typeof(int)); result.Columns.Add("name"); result.Columns.Add("schema_name");
            foreach (string column in new[] { "is_memory_optimized", "is_filetable", "is_tracked_by_cdc", "is_replicated", "is_merge_published", "is_sync_tran_subscribed", "is_remote_data_archive_enabled" }) result.Columns.Add(column, typeof(bool));
            foreach (string column in new[] { "temporal_type", "indexes", "compressed", "security_predicates", "storage_features" }) result.Columns.Add(column, typeof(int));
            var row = result.NewRow(); row["object_id"] = 101; row["name"] = "fixture_rows"; row["schema_name"] = "dbo";
            foreach (DataColumn column in result.Columns)
            {
                if (column.DataType == typeof(bool)) row[column] = false;
                if (column.DataType == typeof(int) && column.ColumnName != "object_id") row[column] = 0;
            }
            result.Rows.Add(row);
        }
        else if (endpoint == "source" && sql.Contains("FROM sys.columns"))
        {
            foreach (string column in new[] { "name", "type_name", "collation_name", "encryption_type" }) result.Columns.Add(column);
            foreach (string column in new[] { "max_length", "precision", "scale", "generated_always_type" }) result.Columns.Add(column, typeof(int));
            foreach (string column in new[] { "is_nullable", "is_identity", "is_computed", "is_sparse", "is_column_set", "is_rowguidcol", "is_ansi_padded", "is_filestream", "is_hidden", "is_masked" }) result.Columns.Add(column, typeof(bool));
            var row = result.NewRow(); row["name"] = "id"; row["type_name"] = "int";
            foreach (DataColumn column in result.Columns)
            {
                if (column.DataType == typeof(bool)) row[column] = column.ColumnName == "is_ansi_padded";
                if (column.DataType == typeof(int)) row[column] = 0;
            }
            row["max_length"] = 4; row["precision"] = 10; result.Rows.Add(row);
        }
        else if (endpoint == "source" && sql.Contains("COUNT_BIG"))
        {
            result.Columns.Add("count", typeof(long)); result.Rows.Add(5L);
        }
        return result;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
