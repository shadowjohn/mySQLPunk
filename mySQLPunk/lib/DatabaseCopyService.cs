using System;
using System.Data;

namespace mySQLPunk.lib
{
    public class DatabaseCopyItem
    {
        public IDatabase Database { get; set; }
        public string DatabaseName { get; set; }
        public string ObjectName { get; set; }
        public string ObjectKind { get; set; }
        public string ProviderName { get; set; }
    }

    public class DatabaseCopyProgress
    {
        public string SourceName { get; set; }
        public string TargetName { get; set; }
        public long CopiedRows { get; set; }
        public long TotalRows { get; set; }
        public string Message { get; set; }
    }

    public class DatabaseCopyResult
    {
        public string TargetName { get; set; }
        public string ObjectKind { get; set; }
        public long CopiedRows { get; set; }
    }

    public class DatabaseCopyService
    {
        private readonly int _batchSize;

        public DatabaseCopyService(int batchSize = 1000)
        {
            _batchSize = batchSize <= 0 ? 1000 : batchSize;
        }

        public DatabaseCopyResult Copy(DatabaseCopyItem source, DatabaseCopyItem target, Action<DatabaseCopyProgress> progress)
        {
            if (source == null || target == null) throw new ArgumentNullException("source");
            if (source.Database == null || target.Database == null) throw new ArgumentException("來源或目標資料庫尚未連線");

            string kind = NormalizeKind(source.ObjectKind);
            bool copyViewAsTable = kind == "view" &&
                !string.Equals(source.ProviderName, target.ProviderName, StringComparison.OrdinalIgnoreCase);
            string targetKind = copyViewAsTable ? "table" : kind;
            string targetName = GenerateTargetName(target.Database, target.DatabaseName, source.ObjectName, targetKind);

            if (kind == "table")
                return CopyTable(source, target, targetName, progress);

            if (kind == "view")
            {
                if (copyViewAsTable)
                    return CopyTable(source, target, targetName, progress);

                return CopyView(source, target, targetName, progress);
            }

            throw new NotSupportedException("只支援複製 Table / View");
        }

        private DatabaseCopyResult CopyTable(DatabaseCopyItem source, DatabaseCopyItem target, string targetName, Action<DatabaseCopyProgress> progress)
        {
            progress?.Invoke(new DatabaseCopyProgress
            {
                SourceName = source.ObjectName,
                TargetName = targetName,
                Message = "讀取來源結構..."
            });

            DataTable columns = source.Database.GetCopyColumns(source.DatabaseName, source.ObjectName);
            if (columns == null || columns.Rows.Count == 0)
                throw new Exception("來源資料表沒有可複製的欄位");

            DataTable indexes = null;
            try { indexes = source.Database.GetCopyIndexes(source.DatabaseName, source.ObjectName); }
            catch { indexes = new DataTable(); }

            target.Database.CreateTableForCopy(target.DatabaseName, targetName, columns, source.ProviderName);

            long total = source.Database.CountRows(source.DatabaseName, source.ObjectName);
            long copied = 0;

            while (copied < total)
            {
                DataTable page = source.Database.SelectTablePage(source.DatabaseName, source.ObjectName, copied, _batchSize);
                if (page == null || page.Rows.Count == 0) break;

                target.Database.InsertTableBatch(target.DatabaseName, targetName, page);
                copied += page.Rows.Count;

                progress?.Invoke(new DatabaseCopyProgress
                {
                    SourceName = source.ObjectName,
                    TargetName = targetName,
                    CopiedRows = copied,
                    TotalRows = total,
                    Message = "Copying " + source.ObjectName + " -> " + targetName + ": " + copied + " / " + total
                });
            }

            try
            {
                target.Database.CreateIndexesForCopy(target.DatabaseName, targetName, indexes, source.ProviderName);
            }
            catch
            {
                // Index 屬於加值資訊，失敗時保留已完成的結構與資料，避免整體複製回滾。
            }

            return new DatabaseCopyResult
            {
                TargetName = targetName,
                ObjectKind = "table",
                CopiedRows = copied
            };
        }

        private DatabaseCopyResult CopyView(DatabaseCopyItem source, DatabaseCopyItem target, string targetName, Action<DatabaseCopyProgress> progress)
        {
            progress?.Invoke(new DatabaseCopyProgress
            {
                SourceName = source.ObjectName,
                TargetName = targetName,
                Message = "讀取 View DDL..."
            });

            string sql = source.Database.GetViewCreateStatement(source.DatabaseName, source.ObjectName);
            if (string.IsNullOrWhiteSpace(sql)) throw new Exception("無法取得 View DDL");

            target.Database.CreateViewFromStatement(target.DatabaseName, targetName, sql);

            return new DatabaseCopyResult
            {
                TargetName = targetName,
                ObjectKind = "view",
                CopiedRows = 0
            };
        }

        private string GenerateTargetName(IDatabase targetDb, string databaseName, string sourceName, string kind)
        {
            string baseName = sourceName + "_copy";
            string candidate = baseName;
            int seq = 1;
            while (ObjectExists(targetDb, databaseName, candidate, kind))
            {
                candidate = baseName + "_" + seq;
                seq++;
            }
            return candidate;
        }

        private static bool ObjectExists(IDatabase db, string databaseName, string name, string kind)
        {
            return kind == "view" ? db.ViewExists(databaseName, name) : db.TableExists(databaseName, name);
        }

        private static string NormalizeKind(string kind)
        {
            if (string.Equals(kind, "views", StringComparison.OrdinalIgnoreCase)) return "view";
            if (string.Equals(kind, "view", StringComparison.OrdinalIgnoreCase)) return "view";
            return "table";
        }
    }
}
