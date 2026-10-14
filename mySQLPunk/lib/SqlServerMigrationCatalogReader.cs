using System;
using System.Data;
using System.Globalization;

namespace mySQLPunk.lib
{
    internal static class SqlServerMigrationCatalogReader
    {
        internal static SqlServerMigrationCatalog Read(Func<string, DataTable> query)
        {
            DataTable engine = query(@"SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)) AS server_name,
CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS version, DB_NAME() AS db_name, DB_ID() AS db_id,
create_date, collation_name FROM sys.databases WHERE database_id=DB_ID()");
            if (engine.Rows.Count != 1) throw new InvalidOperationException("無法讀取引擎與資料庫識別。");
            DataRow identity = engine.Rows[0];
            Version version;
            if (!Version.TryParse(Convert.ToString(identity["version"]), out version)) throw new InvalidOperationException("未知引擎版本。");
            var catalog = new SqlServerMigrationCatalog
            {
                DatabaseName = Convert.ToString(identity["db_name"]), MajorVersion = version.Major,
                Collation = Convert.ToString(identity["collation_name"]),
                Identity = Convert.ToString(identity["server_name"]) + "/" + identity["db_id"] + "/" + Convert.ToDateTime(identity["create_date"]).Ticks
            };
            if (version.Major < 13 || version.Major > 17) { catalog.Blockers.Add("未知或未支援的 SQL Server 引擎。"); return catalog; }
            DataTable permission = query("SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION') AS visible, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL') AS controlled");
            if (Convert.ToString(permission.Rows[0]["visible"]) != "1" || Convert.ToString(permission.Rows[0]["controlled"]) != "1")
            {
                catalog.Blockers.Add("需要既有的資料庫 CONTROL 與 VIEW DEFINITION 權限；工具不會授權或提高權限。");
                return catalog;
            }
            DataTable databaseTriggers = query("SELECT name,is_disabled FROM sys.triggers WHERE parent_class=0");
            foreach (DataRow trigger in databaseTriggers.Rows)
                catalog.Blockers.Add("不支援資料庫層級 DDL trigger（包含停用的 trigger）：" + trigger["name"]);
            DataTable objects = query("SELECT type, name FROM sys.objects WHERE is_ms_shipped=0 ORDER BY object_id");
            catalog.ObjectCount = objects.Rows.Count;
            foreach (DataRow row in objects.Rows)
                if (Convert.ToString(row["type"]).Trim() != "U") catalog.Blockers.Add("不支援物件（含 constraint／trigger／view／procedure／function／sequence）：" + row["name"]);
            DataTable unsupported = query(@"SELECT name FROM sys.types WHERE is_user_defined=1
UNION ALL SELECT name FROM sys.schemas WHERE name NOT IN ('dbo','guest','sys','INFORMATION_SCHEMA','db_owner','db_accessadmin','db_securityadmin','db_ddladmin','db_backupoperator','db_datareader','db_datawriter','db_denydatareader','db_denydatawriter')
UNION ALL SELECT name FROM sys.partition_schemes
UNION ALL SELECT name FROM sys.assemblies WHERE is_user_defined=1");
            foreach (DataRow row in unsupported.Rows) catalog.Blockers.Add("不支援自訂 schema／型別／分割／CLR：" + row[0]);
            if (query("SELECT major_id FROM sys.extended_properties UNION ALL SELECT object_id FROM sys.change_tracking_tables").Rows.Count != 0)
                catalog.Blockers.Add("不支援 extended properties 或 change tracking。");
            if (version.Major >= 14 && query("SELECT object_id FROM sys.tables WHERE is_node=1 OR is_edge=1").Rows.Count != 0)
                catalog.Blockers.Add("不支援 graph tables。");
            if (version.Major >= 16 && query("SELECT object_id FROM sys.tables WHERE ledger_type<>0").Rows.Count != 0)
                catalog.Blockers.Add("不支援 ledger tables。");
            DataTable tables = query(@"SELECT t.object_id,t.name,s.name AS schema_name,t.is_memory_optimized,t.temporal_type,t.is_filetable,t.is_tracked_by_cdc,t.is_replicated,t.is_merge_published,t.is_sync_tran_subscribed,t.is_remote_data_archive_enabled,
 (SELECT COUNT(*) FROM sys.indexes i WHERE i.object_id=t.object_id AND i.index_id>0) AS indexes,
 (SELECT COUNT(*) FROM sys.partitions p WHERE p.object_id=t.object_id AND p.index_id=0 AND p.data_compression<>0) AS compressed,
 (SELECT COUNT(*) FROM sys.security_predicates p WHERE p.target_object_id=t.object_id) AS security_predicates,
 (SELECT COUNT(*) FROM sys.indexes i LEFT JOIN sys.filegroups f ON f.data_space_id=i.data_space_id WHERE i.object_id=t.object_id AND i.index_id=0 AND (f.name IS NULL OR f.name<>'PRIMARY')) AS storage_features
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 ORDER BY t.name");
            foreach (DataRow row in tables.Rows)
            {
                var table = new SqlServerMigrationTable { Name = Convert.ToString(row["name"]) };
                if (Convert.ToString(row["schema_name"]) != "dbo" || Convert.ToBoolean(row["is_memory_optimized"]) ||
                    Convert.ToInt32(row["temporal_type"]) != 0 || Convert.ToBoolean(row["is_filetable"]) ||
                    Convert.ToBoolean(row["is_tracked_by_cdc"]) || Convert.ToBoolean(row["is_replicated"]) || Convert.ToBoolean(row["is_merge_published"]) || Convert.ToBoolean(row["is_sync_tran_subscribed"]) || Convert.ToBoolean(row["is_remote_data_archive_enabled"]) ||
                    Convert.ToInt32(row["indexes"]) != 0 || Convert.ToInt32(row["compressed"]) != 0 || Convert.ToInt32(row["security_predicates"]) != 0 || Convert.ToInt32(row["storage_features"]) != 0)
                    catalog.Blockers.Add("只支援 dbo 未壓縮 heap；禁止索引、記憶體表、temporal、FileTable、RLS：" + table.Name);
                int objectId = Convert.ToInt32(row["object_id"], CultureInfo.InvariantCulture);
                DataTable columns = query(@"SELECT c.name,t.name AS type_name,c.max_length,c.precision,c.scale,c.is_nullable,c.collation_name,
c.is_identity,c.is_computed,c.is_sparse,c.is_column_set,c.is_rowguidcol,c.generated_always_type,c.encryption_type,c.is_ansi_padded,
c.is_filestream,c.is_hidden,ISNULL(m.is_masked,0) AS is_masked FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id
LEFT JOIN sys.masked_columns m ON m.object_id=c.object_id AND m.column_id=c.column_id
WHERE c.object_id=" + objectId.ToString(CultureInfo.InvariantCulture) + " ORDER BY c.column_id");
                foreach (DataRow column in columns.Rows)
                {
                    string type = Convert.ToString(column["type_name"]);
                    int length = Convert.ToInt32(column["max_length"]);
                    if ((type == "char" || type == "varchar" || type == "binary" || type == "varbinary") && !Convert.ToBoolean(column["is_ansi_padded"]))
                        catalog.Blockers.Add("不支援 ANSI_PADDING OFF 欄位：" + table.Name + "." + column["name"]);
                    if (type == "decimal" || type == "numeric") type += "(" + column["precision"] + "," + column["scale"] + ")";
                    else if (type == "datetime2" || type == "time") type += "(" + column["scale"] + ")";
                    else if (type == "char" || type == "varchar" || type == "binary" || type == "varbinary" || type == "nchar" || type == "nvarchar")
                        type += "(" + ((type == "nchar" || type == "nvarchar") ? length / 2 : length) + ")";
                    foreach (string flag in new[] { "is_identity", "is_computed", "is_sparse", "is_column_set", "is_rowguidcol", "is_filestream", "is_hidden", "is_masked" })
                        if (Convert.ToBoolean(column[flag])) catalog.Blockers.Add("不支援欄位 feature " + flag + "：" + table.Name + "." + column["name"]);
                    if (Convert.ToInt32(column["generated_always_type"]) != 0 || !(column["encryption_type"] is DBNull))
                        catalog.Blockers.Add("不支援 generated／encrypted 欄位：" + table.Name + "." + column["name"]);
                    if (!(column["collation_name"] is DBNull) && Convert.ToString(column["collation_name"]) != catalog.Collation)
                        catalog.Blockers.Add("欄位定序不是資料庫預設定序：" + table.Name + "." + column["name"]);
                    if (length > 8000) catalog.Blockers.Add("不支援長欄位：" + table.Name + "." + column["name"]);
                    table.Columns.Add(new SqlServerMigrationColumn { Name = Convert.ToString(column["name"]), TypeSql = type, Nullable = Convert.ToBoolean(column["is_nullable"]) });
                }
                DataTable count = query("SELECT COUNT_BIG(*) FROM [dbo]." + SqlServerCompatibilityMigration.Quote(table.Name));
                table.RowCount = Convert.ToInt64(count.Rows[0][0], CultureInfo.InvariantCulture);
                catalog.Tables.Add(table);
            }
            return catalog;
        }
    }
}
