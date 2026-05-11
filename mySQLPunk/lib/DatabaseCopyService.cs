using System;
using System.Data;
using System.Text.RegularExpressions;

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

    public enum ViewCopyFallback
    {
        AutoSnapshot,       // 嘗試轉換 SQL，失敗改用 table snapshot
        ForceTableSnapshot, // 跨過轉換，直接建立 table snapshot
    }

    public class DatabaseCopyService
    {
        private readonly int _batchSize;

        public DatabaseCopyService(int batchSize = 1000)
        {
            _batchSize = batchSize <= 0 ? 1000 : batchSize;
        }

        public DatabaseCopyResult Copy(DatabaseCopyItem source, DatabaseCopyItem target, Action<DatabaseCopyProgress> progress, ViewCopyFallback viewFallback = ViewCopyFallback.AutoSnapshot)
        {
            if (source == null || target == null) throw new ArgumentNullException("source");
            if (source.Database == null || target.Database == null) throw new ArgumentException("來源或目標資料庫尚未連線");

            string kind = NormalizeKind(source.ObjectKind);
            bool crossProviderView = kind == "view" &&
                !string.Equals(source.ProviderName, target.ProviderName, StringComparison.OrdinalIgnoreCase);
            string targetKind = crossProviderView ? "view" : kind;
            string targetName = GenerateTargetName(target.Database, target.DatabaseName, source.ObjectName, targetKind);

            if (kind == "table")
                return CopyTable(source, target, targetName, progress);

            if (kind == "view")
            {
                if (crossProviderView)
                {
                    if (viewFallback == ViewCopyFallback.ForceTableSnapshot)
                    {
                        progress?.Invoke(new DatabaseCopyProgress
                        {
                            SourceName = source.ObjectName,
                            TargetName = targetName,
                            Message = "使用者選擇直接建立 table snapshot"
                        });
                        targetName = GenerateTargetName(target.Database, target.DatabaseName, source.ObjectName, "table");
                        return CopyTable(source, target, targetName, progress);
                    }

                    DatabaseCopyResult convertedView;
                    if (TryCopyConvertedView(source, target, targetName, progress, out convertedView))
                    {
                        return convertedView;
                    }

                    targetName = GenerateTargetName(target.Database, target.DatabaseName, source.ObjectName, "table");
                    return CopyTable(source, target, targetName, progress);
                }

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

        private bool TryCopyConvertedView(DatabaseCopyItem source, DatabaseCopyItem target, string targetName, Action<DatabaseCopyProgress> progress, out DatabaseCopyResult result)
        {
            result = null;

            progress?.Invoke(new DatabaseCopyProgress
            {
                SourceName = source.ObjectName,
                TargetName = targetName,
                Message = "轉換 View SQL..."
            });

            string sourceSql = source.Database.GetViewCreateStatement(source.DatabaseName, source.ObjectName);
            string convertedSql;
            string reason;
            if (!ViewSqlDialectConverter.TryConvertSelectForTarget(sourceSql, source.ProviderName, target.ProviderName, out convertedSql, out reason))
            {
                progress?.Invoke(new DatabaseCopyProgress
                {
                    SourceName = source.ObjectName,
                    TargetName = targetName,
                    Message = "View SQL 無法安全轉換，改用 table snapshot：" + reason
                });
                return false;
            }

            try
            {
                target.Database.CreateViewFromStatement(target.DatabaseName, targetName, convertedSql);
                result = new DatabaseCopyResult
                {
                    TargetName = targetName,
                    ObjectKind = "view",
                    CopiedRows = 0
                };
                return true;
            }
            catch (Exception ex)
            {
                progress?.Invoke(new DatabaseCopyProgress
                {
                    SourceName = source.ObjectName,
                    TargetName = targetName,
                    Message = "View 建立失敗，改用 table snapshot：" + ex.Message
                });
                return false;
            }
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

    internal static class ViewSqlDialectConverter
    {
        public static bool TryConvertSelectForTarget(string sourceViewSql, string sourceProvider, string targetProvider, out string convertedSql, out string reason)
        {
            convertedSql = "";
            reason = "";

            string selectSql = ExtractSelectSql(sourceViewSql);
            if (string.IsNullOrWhiteSpace(selectSql))
            {
                reason = "無法解析 SELECT SQL";
                return false;
            }

            if (ContainsUnsupportedFeature(selectSql, targetProvider, out reason))
            {
                return false;
            }

            convertedSql = NormalizeIdentifiersForTarget(selectSql, targetProvider).Trim().TrimEnd(';').Trim();
            return !string.IsNullOrWhiteSpace(convertedSql);
        }

        public static string ExtractSelectSql(string sourceViewSql)
        {
            if (string.IsNullOrWhiteSpace(sourceViewSql)) return "";

            string sql = sourceViewSql.Trim().TrimEnd(';').Trim();
            if (StartsWithQuery(sql)) return sql;

            Match match = Regex.Match(
                sql,
                @"\bAS\s+(?<body>(?:SELECT|WITH)\b.*)$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            return match.Success ? match.Groups["body"].Value.Trim().TrimEnd(';').Trim() : "";
        }

        private static bool StartsWithQuery(string sql)
        {
            return sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                   sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsUnsupportedFeature(string selectSql, string targetProvider, out string reason)
        {
            reason = "";
            string provider = NormalizeProvider(targetProvider);

            if (!provider.Equals("mssql", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(selectSql, @"\bTOP\s+\(?\d+", RegexOptions.IgnoreCase))
            {
                reason = "TOP 語法不是目標資料庫通用語法";
                return true;
            }

            if (!provider.Equals("oracle", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(selectSql, @"\b(CONNECT\s+BY|START\s+WITH|ROWNUM)\b", RegexOptions.IgnoreCase))
            {
                reason = "Oracle 階層查詢或 ROWNUM 無法自動轉換";
                return true;
            }

            if (!provider.Equals("mysql", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(selectSql, @"\bSQL\s+SECURITY\b|@", RegexOptions.IgnoreCase))
            {
                reason = "MySQL 專用 View 語法無法自動轉換";
                return true;
            }

            return false;
        }

        private static string NormalizeIdentifiersForTarget(string selectSql, string targetProvider)
        {
            string provider = NormalizeProvider(targetProvider);
            string openQuote = "\"";
            string closeQuote = "\"";

            if (provider == "mysql")
            {
                openQuote = "`";
                closeQuote = "`";
            }
            else if (provider == "mssql")
            {
                openQuote = "[";
                closeQuote = "]";
            }

            string sql = Regex.Replace(selectSql, @"`([^`]+)`", m => QuoteIdentifier(m.Groups[1].Value, openQuote, closeQuote));
            sql = Regex.Replace(sql, @"\[([^\]]+)\]", m => QuoteIdentifier(m.Groups[1].Value, openQuote, closeQuote));

            if (provider == "mysql" || provider == "mssql")
            {
                sql = Regex.Replace(sql, @"""([^""]+)""", m => QuoteIdentifier(m.Groups[1].Value, openQuote, closeQuote));
            }

            if (provider != "postgresql")
            {
                sql = Regex.Replace(sql, @"\bpublic\.", "", RegexOptions.IgnoreCase);
                sql = Regex.Replace(sql, @"(""public""|`public`|\[public\])\.", "", RegexOptions.IgnoreCase);
            }

            if (provider != "mssql")
            {
                sql = Regex.Replace(sql, @"\bdbo\.", "", RegexOptions.IgnoreCase);
                sql = Regex.Replace(sql, @"(""dbo""|`dbo`|\[dbo\])\.", "", RegexOptions.IgnoreCase);
            }

            return sql;
        }

        private static string QuoteIdentifier(string name, string openQuote, string closeQuote)
        {
            if (openQuote == "[") return "[" + name.Replace("]", "]]") + "]";
            return openQuote + name.Replace(openQuote, openQuote + openQuote) + closeQuote;
        }

        private static string NormalizeProvider(string provider)
        {
            if (string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase)) return "mssql";
            return (provider ?? "").ToLowerInvariant();
        }
    }
}
