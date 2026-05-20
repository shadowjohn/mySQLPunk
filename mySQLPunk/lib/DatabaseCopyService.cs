using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
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

    public class ViewSqlConversionPreview
    {
        public string SourceSql { get; set; }
        public string ConvertedSql { get; set; }
        public string Reason { get; set; }
        public bool CanConvert { get; set; }
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
        public static ViewSqlConversionPreview BuildPreview(string sourceViewSql, string sourceProvider, string targetProvider)
        {
            string convertedSql;
            string reason;
            bool canConvert = TryConvertSelectForTarget(sourceViewSql, sourceProvider, targetProvider, out convertedSql, out reason);
            return new ViewSqlConversionPreview
            {
                SourceSql = sourceViewSql ?? "",
                ConvertedSql = convertedSql ?? "",
                Reason = reason ?? "",
                CanConvert = canConvert
            };
        }

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

            if (!TryRewritePortableSyntax(selectSql, sourceProvider, targetProvider, out selectSql, out reason))
            {
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

        private static bool TryRewritePortableSyntax(string selectSql, string sourceProvider, string targetProvider, out string rewrittenSql, out string reason)
        {
            reason = "";
            rewrittenSql = (selectSql ?? "").Trim().TrimEnd(';').Trim();
            string provider = NormalizeProvider(targetProvider);

            if (!TryRewriteTopClause(rewrittenSql, provider, out rewrittenSql, out reason))
                return false;
            if (!TryRewriteLimitClause(rewrittenSql, provider, out rewrittenSql, out reason))
                return false;
            if (!TryRewriteRownumPredicate(rewrittenSql, provider, out rewrittenSql, out reason))
                return false;

            rewrittenSql = RewriteRecursiveCteKeyword(rewrittenSql, provider);
            rewrittenSql = RewriteCommonFunctions(rewrittenSql, NormalizeProvider(sourceProvider), provider);
            return true;
        }

        private static string RewriteRecursiveCteKeyword(string selectSql, string targetProvider)
        {
            if (targetProvider != "mssql" && targetProvider != "oracle") return selectSql;

            return Regex.Replace(
                selectSql,
                @"^\s*WITH\s+RECURSIVE\b",
                "WITH",
                RegexOptions.IgnoreCase);
        }

        private static bool TryRewriteTopClause(string selectSql, string targetProvider, out string rewrittenSql, out string reason)
        {
            rewrittenSql = selectSql;
            reason = "";

            Match match = Regex.Match(
                selectSql,
                @"^\s*SELECT\s+(?<distinct>DISTINCT\s+)?TOP\s*\(?(?<limit>\d+)\)?\s+(?<body>.+)$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return true;
            if (targetProvider == "mssql") return true;

            string body = match.Groups["body"].Value.Trim();
            string distinct = match.Groups["distinct"].Value;
            string withoutTop = "SELECT " + distinct + body;
            rewrittenSql = AppendRowLimit(withoutTop, targetProvider, match.Groups["limit"].Value);
            return true;
        }

        private static bool TryRewriteLimitClause(string selectSql, string targetProvider, out string rewrittenSql, out string reason)
        {
            rewrittenSql = selectSql;
            reason = "";

            Match match = Regex.Match(
                selectSql,
                @"\s+LIMIT\s+(?<first>\d+)(?:\s*,\s*(?<second>\d+)|\s+OFFSET\s+(?<offset>\d+))?\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return true;

            string limit = match.Groups["second"].Success ? match.Groups["second"].Value : match.Groups["first"].Value;
            string offset = match.Groups["second"].Success
                ? match.Groups["first"].Value
                : (match.Groups["offset"].Success ? match.Groups["offset"].Value : "");
            string body = selectSql.Substring(0, match.Index).Trim();

            if (targetProvider == "mssql")
            {
                if (!string.IsNullOrWhiteSpace(offset) && offset != "0")
                {
                    if (!HasOrderByClause(body))
                    {
                        reason = "LIMIT OFFSET 轉 SQL Server 需要穩定 ORDER BY，無法安全自動轉換";
                        return false;
                    }

                    rewrittenSql = body + " OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY";
                    return true;
                }

                rewrittenSql = InsertSqlServerTop(body, limit);
                return true;
            }

            if (targetProvider == "oracle")
            {
                rewrittenSql = string.IsNullOrWhiteSpace(offset) || offset == "0"
                    ? body + " FETCH FIRST " + limit + " ROWS ONLY"
                    : body + " OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY";
                return true;
            }

            rewrittenSql = selectSql;
            return true;
        }

        private static bool HasOrderByClause(string selectSql)
        {
            return Regex.IsMatch(selectSql ?? string.Empty, @"\bORDER\s+BY\b", RegexOptions.IgnoreCase);
        }

        private static bool TryRewriteRownumPredicate(string selectSql, string targetProvider, out string rewrittenSql, out string reason)
        {
            rewrittenSql = selectSql;
            reason = "";
            if (targetProvider == "oracle") return true;

            Match limitMatch = Regex.Match(selectSql, @"\bROWNUM\s*<=\s*(?<limit>\d+)\b", RegexOptions.IgnoreCase);
            if (!limitMatch.Success) return true;

            string limit = limitMatch.Groups["limit"].Value;
            string body = selectSql;
            body = Regex.Replace(body, @"\s+WHERE\s+ROWNUM\s*<=\s*\d+\s+AND\s+", " WHERE ", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"\s+AND\s+ROWNUM\s*<=\s*\d+\b", "", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"\s+WHERE\s+ROWNUM\s*<=\s*\d+\b", "", RegexOptions.IgnoreCase);

            if (Regex.IsMatch(body, @"\bROWNUM\b", RegexOptions.IgnoreCase))
            {
                reason = "ROWNUM 條件過於複雜，無法安全自動轉換";
                return false;
            }

            rewrittenSql = AppendRowLimit(body.Trim(), targetProvider, limit);
            return true;
        }

        private static string RewriteCommonFunctions(string selectSql, string sourceProvider, string targetProvider)
        {
            string sql = selectSql;

            if (targetProvider != "oracle")
                sql = Regex.Replace(sql, @"\bNVL\s*\(", "COALESCE(", RegexOptions.IgnoreCase);
            if (targetProvider != "mysql" && targetProvider != "sqlite")
                sql = Regex.Replace(sql, @"\bIFNULL\s*\(", "COALESCE(", RegexOptions.IgnoreCase);
            if (targetProvider != "mssql")
                sql = Regex.Replace(sql, @"\bISNULL\s*\(", "COALESCE(", RegexOptions.IgnoreCase);

            sql = RewriteCurrentDateTimeFunctions(sql, targetProvider);
            sql = RewriteDateFormatFunctions(sql, targetProvider);
            sql = RewriteDateDiffFunctions(sql, targetProvider);
            sql = RewriteDateAddFunctions(sql, targetProvider);
            sql = RewriteDatePartFunctions(sql, targetProvider);
            sql = RewriteConditionalFunctions(sql, targetProvider);
            sql = RewriteConcatFunctions(sql, targetProvider);
            sql = RewriteStringLengthFunctions(sql, targetProvider);
            sql = RewriteSubstringFunctions(sql, targetProvider);
            sql = RewriteEdgeSubstringFunctions(sql, targetProvider);
            sql = RewriteStringPositionFunctions(sql, targetProvider);
            sql = RewriteStringAggregateFunctions(sql, targetProvider);
            sql = RewriteJsonValueFunctions(sql, targetProvider);
            sql = RewriteJsonExtractFunctions(sql, targetProvider);

            return sql;
        }

        private static string RewriteDatePartFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bDATEPART\s*\(\s*(?<part>year|yy|yyyy|month|mm|m|day|dd|d)\s*,\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => BuildDatePartExpression(targetProvider, NormalizeDatePart(m.Groups["part"].Value), m.Groups["expr"].Value.Trim()),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bEXTRACT\s*\(\s*(?<part>YEAR|MONTH|DAY)\s+FROM\s+(?<expr>[^()]+?)\s*\)",
                m => BuildDatePartExpression(targetProvider, NormalizeDatePart(m.Groups["part"].Value), m.Groups["expr"].Value.Trim()),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\b(?<func>YEAR|MONTH|DAY)\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => BuildDatePartExpression(targetProvider, NormalizeDatePart(m.Groups["func"].Value), m.Groups["expr"].Value.Trim()),
                RegexOptions.IgnoreCase);

            return sql;
        }

        private static string BuildDatePartExpression(string targetProvider, string part, string expr)
        {
            if (targetProvider == "mssql" || targetProvider == "mysql")
            {
                return part.ToUpperInvariant() + "(" + expr + ")";
            }

            if (targetProvider == "sqlite")
            {
                return "CAST(strftime('" + GetSqliteDatePartFormat(part) + "', " + expr + ") AS INTEGER)";
            }

            return "EXTRACT(" + part.ToUpperInvariant() + " FROM " + expr + ")";
        }

        private static string NormalizeDatePart(string part)
        {
            string text = (part ?? string.Empty).Trim().Trim('\'', '"', '[', ']').ToLowerInvariant();
            if (text == "yy" || text == "yyyy") return "year";
            if (text == "mm" || text == "m") return "month";
            if (text == "dd" || text == "d") return "day";
            return text;
        }

        private static string GetSqliteDatePartFormat(string part)
        {
            if (part == "year") return "%Y";
            if (part == "month") return "%m";
            return "%d";
        }

        private static string RewriteConditionalFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bIIF\s*\((?<args>[^()]*)\)",
                m => RewriteConditionalFunction(m, targetProvider),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bIF\s*\((?<args>[^()]*)\)",
                m => RewriteConditionalFunction(m, targetProvider),
                RegexOptions.IgnoreCase);

            return sql;
        }

        private static string RewriteConditionalFunction(Match match, string targetProvider)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count != 3) return match.Value;

            string condition = args[0];
            string whenTrue = args[1];
            string whenFalse = args[2];
            if (targetProvider == "mysql") return "IF(" + condition + ", " + whenTrue + ", " + whenFalse + ")";
            if (targetProvider == "mssql") return "IIF(" + condition + ", " + whenTrue + ", " + whenFalse + ")";
            return "CASE WHEN " + condition + " THEN " + whenTrue + " ELSE " + whenFalse + " END";
        }

        private static string RewriteCurrentDateTimeFunctions(string selectSql, string targetProvider)
        {
            string sql = selectSql;
            if (targetProvider != "mssql")
            {
                sql = Regex.Replace(sql, @"\bGETDATE\s*\(\s*\)", "CURRENT_TIMESTAMP", RegexOptions.IgnoreCase);
            }

            if (targetProvider != "mysql")
            {
                sql = Regex.Replace(sql, @"\bNOW\s*\(\s*\)", "CURRENT_TIMESTAMP", RegexOptions.IgnoreCase);
            }

            sql = Regex.Replace(
                sql,
                @"\bCURDATE\s*\(\s*\)",
                m => BuildCurrentDateExpression(targetProvider),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bCURRENT_DATE\s*(?:\(\s*\))?",
                m => BuildCurrentDateExpression(targetProvider),
                RegexOptions.IgnoreCase);

            return sql;
        }

        private static string BuildCurrentDateExpression(string targetProvider)
        {
            if (targetProvider == "mssql") return "CAST(GETDATE() AS date)";
            if (targetProvider == "mysql") return "CURDATE()";
            return "CURRENT_DATE";
        }

        private static string RewriteDateFormatFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bDATE_FORMAT\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<format>[^']+)'\s*\)",
                m =>
                {
                    string expr = m.Groups["expr"].Value.Trim();
                    string format = m.Groups["format"].Value;
                    if (targetProvider == "mysql") return m.Value;
                    return BuildDateFormatExpression(targetProvider, expr, TranslateMySqlDateFormatPattern(format, targetProvider));
                },
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bFORMAT\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<format>[^']+)'\s*\)",
                m =>
                {
                    string expr = m.Groups["expr"].Value.Trim();
                    string format = m.Groups["format"].Value;
                    if (targetProvider == "mssql") return m.Value;
                    return BuildDateFormatExpression(targetProvider, expr, TranslateDotNetDateFormatPattern(format, targetProvider));
                },
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bTO_CHAR\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<format>[^']+)'\s*\)",
                m =>
                {
                    string expr = m.Groups["expr"].Value.Trim();
                    string format = m.Groups["format"].Value;
                    if (targetProvider == "oracle" || targetProvider == "postgresql") return m.Value;
                    return BuildDateFormatExpression(targetProvider, expr, TranslateOracleDateFormatPattern(format, targetProvider));
                },
                RegexOptions.IgnoreCase);

            return sql;
        }

        private static string RewriteDateDiffFunctions(string selectSql, string targetProvider)
        {
            return Regex.Replace(
                selectSql,
                @"\bDATEDIFF\s*\((?<args>[^()]*)\)",
                m => RewriteDateDiffFunction(m, targetProvider),
                RegexOptions.IgnoreCase);
        }

        private static string RewriteDateDiffFunction(Match match, string targetProvider)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count == 2)
            {
                return BuildDateDiffDaysExpression(targetProvider, args[0], args[1]);
            }

            if (args.Count == 3 && IsDayDatePart(args[0]))
            {
                return BuildDateDiffDaysExpression(targetProvider, args[2], args[1]);
            }

            return match.Value;
        }

        private static string RewriteDateAddFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bDATE_ADD\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*INTERVAL\s+(?<amount>-?\d+)\s+DAY\s*\)",
                m => BuildDateAddDaysExpression(targetProvider, m.Groups["expr"].Value.Trim(), m.Groups["amount"].Value),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bDATE_SUB\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*INTERVAL\s+(?<amount>-?\d+)\s+DAY\s*\)",
                m => BuildDateAddDaysExpression(targetProvider, m.Groups["expr"].Value.Trim(), NegateIntegerString(m.Groups["amount"].Value)),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bDATEADD\s*\(\s*(?<part>day|dd|d)\s*,\s*(?<amount>-?\d+)\s*,\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => BuildDateAddDaysExpression(targetProvider, m.Groups["expr"].Value.Trim(), m.Groups["amount"].Value),
                RegexOptions.IgnoreCase);

            return sql;
        }

        private static string BuildDateAddDaysExpression(string targetProvider, string expr, string amount)
        {
            if (targetProvider == "mssql") return "DATEADD(day, " + amount + ", " + expr + ")";
            if (targetProvider == "mysql") return "DATE_ADD(" + expr + ", INTERVAL " + amount + " DAY)";
            if (targetProvider == "sqlite") return "date(" + expr + ", '" + BuildSqliteDayModifier(amount) + " day')";
            if (targetProvider == "oracle") return expr + " + " + amount;
            return expr + " + INTERVAL '" + amount + " day'";
        }

        private static string BuildSqliteDayModifier(string amount)
        {
            return amount.StartsWith("-", StringComparison.Ordinal) ? amount : "+" + amount;
        }

        private static string NegateIntegerString(string amount)
        {
            string text = (amount ?? "0").Trim();
            if (text.Length == 0) return "0";
            if (IsZeroIntegerString(text)) return "0";
            return text.StartsWith("-", StringComparison.Ordinal) ? text.Substring(1) : "-" + text;
        }

        private static bool IsZeroIntegerString(string value)
        {
            string text = (value ?? string.Empty).Trim().TrimStart('-');
            if (text.Length == 0) return false;
            foreach (char ch in text)
            {
                if (ch != '0') return false;
            }
            return true;
        }

        private static string BuildDateDiffDaysExpression(string targetProvider, string endDate, string startDate)
        {
            if (targetProvider == "mssql") return "DATEDIFF(day, " + startDate + ", " + endDate + ")";
            if (targetProvider == "mysql") return "DATEDIFF(" + endDate + ", " + startDate + ")";
            if (targetProvider == "sqlite") return "CAST(julianday(" + endDate + ") - julianday(" + startDate + ") AS INTEGER)";
            if (targetProvider == "oracle") return "TRUNC(" + endDate + ") - TRUNC(" + startDate + ")";
            return "(" + endDate + "::date - " + startDate + "::date)";
        }

        private static bool IsDayDatePart(string value)
        {
            string text = (value ?? string.Empty).Trim().Trim('\'', '"', '[', ']').ToLowerInvariant();
            return text == "day" || text == "dd" || text == "d";
        }

        private static string RewriteConcatFunctions(string selectSql, string targetProvider)
        {
            if (targetProvider == "mysql" || targetProvider == "mssql") return selectSql;

            return Regex.Replace(
                selectSql,
                @"\bCONCAT\s*\((?<args>[^()]*)\)",
                m =>
                {
                    List<string> args = SplitFunctionArguments(m.Groups["args"].Value);
                    if (args.Count < 2) return m.Value;
                    return string.Join(" || ", args.ToArray());
                },
                RegexOptions.IgnoreCase);
        }

        private static string RewriteStringLengthFunctions(string selectSql, string targetProvider)
        {
            if (targetProvider == "mssql")
            {
                return Regex.Replace(
                    selectSql,
                    @"\bLENGTH\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                    m => "LEN(" + m.Groups["expr"].Value.Trim() + ")",
                    RegexOptions.IgnoreCase);
            }

            return Regex.Replace(
                selectSql,
                @"\bLEN\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => "LENGTH(" + m.Groups["expr"].Value.Trim() + ")",
                RegexOptions.IgnoreCase);
        }

        private static string RewriteSubstringFunctions(string selectSql, string targetProvider)
        {
            if (targetProvider == "mssql")
            {
                return Regex.Replace(
                    selectSql,
                    @"\bSUBSTR\s*\((?<args>[^()]*)\)",
                    m => RewriteFunctionName(m, "SUBSTRING"),
                    RegexOptions.IgnoreCase);
            }

            if (targetProvider == "oracle" || targetProvider == "sqlite")
            {
                return Regex.Replace(
                    selectSql,
                    @"\bSUBSTRING\s*\((?<args>[^()]*)\)",
                    m => RewriteFunctionName(m, "SUBSTR"),
                    RegexOptions.IgnoreCase);
            }

            return selectSql;
        }

        private static string RewriteFunctionName(Match match, string functionName)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count < 2) return match.Value;
            return functionName + "(" + string.Join(", ", args.ToArray()) + ")";
        }

        private static string RewriteEdgeSubstringFunctions(string selectSql, string targetProvider)
        {
            if (targetProvider != "oracle" && targetProvider != "sqlite") return selectSql;

            string sql = Regex.Replace(
                selectSql,
                @"\bLEFT\s*\((?<args>[^()]*)\)",
                m => RewriteLeftFunction(m),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bRIGHT\s*\((?<args>[^()]*)\)",
                m => RewriteRightFunction(m),
                RegexOptions.IgnoreCase);

            return sql;
        }

        private static string RewriteLeftFunction(Match match)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count != 2) return match.Value;
            return "SUBSTR(" + args[0] + ", 1, " + args[1] + ")";
        }

        private static string RewriteRightFunction(Match match)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count != 2) return match.Value;
            return "SUBSTR(" + args[0] + ", -" + args[1] + ")";
        }

        private static string RewriteStringPositionFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bLOCATE\s*\((?<args>[^()]*)\)",
                m => RewriteStringPositionFromNeedleHaystack(m, targetProvider),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bCHARINDEX\s*\((?<args>[^()]*)\)",
                m => RewriteStringPositionFromNeedleHaystack(m, targetProvider),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bINSTR\s*\((?<args>[^()]*)\)",
                m => RewriteStringPositionFromHaystackNeedle(m, targetProvider),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bPOSITION\s*\(\s*(?<needle>[^()]+?)\s+IN\s+(?<haystack>[^()]+?)\s*\)",
                m => BuildStringPositionFunction(targetProvider, m.Groups["needle"].Value.Trim(), m.Groups["haystack"].Value.Trim()),
                RegexOptions.IgnoreCase);

            return sql;
        }

        private static string RewriteStringPositionFromNeedleHaystack(Match match, string targetProvider)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count < 2) return match.Value;
            return BuildStringPositionFunction(targetProvider, args[0], args[1]);
        }

        private static string RewriteStringPositionFromHaystackNeedle(Match match, string targetProvider)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count < 2) return match.Value;
            return BuildStringPositionFunction(targetProvider, args[1], args[0]);
        }

        private static string BuildStringPositionFunction(string targetProvider, string needle, string haystack)
        {
            if (targetProvider == "mysql") return "LOCATE(" + needle + ", " + haystack + ")";
            if (targetProvider == "mssql") return "CHARINDEX(" + needle + ", " + haystack + ")";
            if (targetProvider == "postgresql") return "POSITION(" + needle + " IN " + haystack + ")";
            return "INSTR(" + haystack + ", " + needle + ")";
        }

        private static string RewriteStringAggregateFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bGROUP_CONCAT\s*\(\s*(?<expr>[^()]*?)(?:\s+SEPARATOR\s*'(?<sep>[^']*)')?\s*\)",
                m => BuildStringAggregate(targetProvider, m.Groups["expr"].Value.Trim(), m.Groups["sep"].Success ? m.Groups["sep"].Value : ","),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bgroup_concat\s*\(\s*(?![^)]*\bSEPARATOR\b)(?<expr>[^,()]+(?:\([^)]*\))?)\s*(?:,\s*'(?<sep>[^']*)')?\s*\)",
                m => BuildStringAggregate(targetProvider, m.Groups["expr"].Value.Trim(), m.Groups["sep"].Success ? m.Groups["sep"].Value : ","),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bSTRING_AGG\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<sep>[^']*)'\s*\)",
                m => BuildStringAggregate(targetProvider, m.Groups["expr"].Value.Trim(), m.Groups["sep"].Value),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bLISTAGG\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<sep>[^']*)'\s*\)\s+WITHIN\s+GROUP\s*\(\s*ORDER\s+BY\s+(?<order>[^)]+)\)",
                m => BuildStringAggregate(targetProvider, m.Groups["expr"].Value.Trim(), m.Groups["sep"].Value),
                RegexOptions.IgnoreCase);

            return sql;
        }

        private static string RewriteJsonValueFunctions(string selectSql, string targetProvider)
        {
            return Regex.Replace(
                selectSql,
                @"\bJSON_VALUE\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<path>\$[^']*)'\s*\)",
                m =>
                {
                    string expr = m.Groups["expr"].Value.Trim();
                    string path = m.Groups["path"].Value;
                    string escapedPath = EscapeSqlString(path);
                    if (targetProvider == "mysql") return "JSON_UNQUOTE(JSON_EXTRACT(" + expr + ", '" + escapedPath + "'))";
                    if (targetProvider == "postgresql")
                    {
                        string pgPath = BuildPostgreSqlJsonTextPath(path);
                        if (!string.IsNullOrWhiteSpace(pgPath)) return expr + " #>> " + pgPath;
                    }
                    if (targetProvider == "sqlite") return "json_extract(" + expr + ", '" + escapedPath + "')";
                    return m.Value;
                },
                RegexOptions.IgnoreCase);
        }

        private static string RewriteJsonExtractFunctions(string selectSql, string targetProvider)
        {
            return Regex.Replace(
                selectSql,
                @"\bJSON_EXTRACT\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<path>\$[^']*)'\s*\)",
                m =>
                {
                    string expr = m.Groups["expr"].Value.Trim();
                    string path = m.Groups["path"].Value;
                    string escapedPath = EscapeSqlString(path);
                    if (targetProvider == "mssql" || targetProvider == "oracle") return "JSON_VALUE(" + expr + ", '" + escapedPath + "')";
                    if (targetProvider == "postgresql")
                    {
                        string pgPath = BuildPostgreSqlJsonTextPath(path);
                        if (!string.IsNullOrWhiteSpace(pgPath)) return expr + " #>> " + pgPath;
                    }
                    if (targetProvider == "sqlite") return "json_extract(" + expr + ", '" + escapedPath + "')";
                    return m.Value;
                },
                RegexOptions.IgnoreCase);
        }

        private static string BuildPostgreSqlJsonTextPath(string jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath) || !jsonPath.StartsWith("$.", StringComparison.Ordinal)) return "";

            List<string> parts = new List<string>();
            string body = jsonPath.Substring(2);
            int index = 0;
            while (index < body.Length)
            {
                if (body[index] == '.')
                {
                    index++;
                    continue;
                }

                if (body[index] == '[')
                {
                    int end = body.IndexOf(']', index + 1);
                    if (end <= index + 1) return "";
                    string arrayIndex = body.Substring(index + 1, end - index - 1);
                    if (!Regex.IsMatch(arrayIndex, @"^\d+$")) return "";
                    parts.Add(arrayIndex);
                    index = end + 1;
                    continue;
                }

                int start = index;
                while (index < body.Length && body[index] != '.' && body[index] != '[')
                {
                    index++;
                }

                string key = body.Substring(start, index - start);
                if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(new[] { '{', '}', ',', '\'', '"' }) >= 0) return "";
                parts.Add(key);
            }

            return parts.Count == 0 ? "" : "'{" + string.Join(",", parts.ToArray()) + "}'";
        }

        private static List<string> SplitFunctionArguments(string argsText)
        {
            List<string> args = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inSingleQuote = false;
            bool inDoubleQuote = false;

            for (int i = 0; i < (argsText ?? string.Empty).Length; i++)
            {
                char ch = argsText[i];
                if (ch == '\'' && !inDoubleQuote)
                {
                    current.Append(ch);
                    if (inSingleQuote && i + 1 < argsText.Length && argsText[i + 1] == '\'')
                    {
                        current.Append(argsText[++i]);
                    }
                    else
                    {
                        inSingleQuote = !inSingleQuote;
                    }
                }
                else if (ch == '"' && !inSingleQuote)
                {
                    current.Append(ch);
                    inDoubleQuote = !inDoubleQuote;
                }
                else if (ch == ',' && !inSingleQuote && !inDoubleQuote)
                {
                    string item = current.ToString().Trim();
                    if (item.Length > 0) args.Add(item);
                    current.Length = 0;
                }
                else
                {
                    current.Append(ch);
                }
            }

            string tail = current.ToString().Trim();
            if (tail.Length > 0) args.Add(tail);
            return args;
        }

        private static string BuildStringAggregate(string targetProvider, string expr, string separator)
        {
            string sep = EscapeSqlString(separator);
            if (targetProvider == "mysql") return "GROUP_CONCAT(" + expr + " SEPARATOR '" + sep + "')";
            if (targetProvider == "sqlite") return "group_concat(" + expr + ", '" + sep + "')";
            if (targetProvider == "oracle") return "LISTAGG(" + expr + ", '" + sep + "') WITHIN GROUP (ORDER BY " + expr + ")";
            return "STRING_AGG(" + expr + ", '" + sep + "')";
        }

        private static string BuildDateFormatExpression(string targetProvider, string expr, string translatedPattern)
        {
            string escapedPattern = EscapeSqlString(translatedPattern);
            if (targetProvider == "mysql") return "DATE_FORMAT(" + expr + ", '" + escapedPattern + "')";
            if (targetProvider == "sqlite") return "strftime('" + escapedPattern + "', " + expr + ")";
            if (targetProvider == "mssql") return "FORMAT(" + expr + ", '" + escapedPattern + "')";
            return "TO_CHAR(" + expr + ", '" + escapedPattern + "')";
        }

        private static string TranslateMySqlDateFormatPattern(string format, string targetProvider)
        {
            string translated = format ?? "";
            if (targetProvider == "mssql")
            {
                return ReplaceDateFormatTokens(
                    translated,
                    new[] { "%Y", "%y", "%m", "%c", "%d", "%e", "%H", "%h", "%i", "%s" },
                    new[] { "yyyy", "yy", "MM", "M", "dd", "d", "HH", "hh", "mm", "ss" });
            }

            if (targetProvider == "mysql" || targetProvider == "sqlite") return translated;

            return ReplaceDateFormatTokens(
                translated,
                new[] { "%Y", "%y", "%m", "%c", "%d", "%e", "%H", "%h", "%i", "%s" },
                new[] { "YYYY", "YY", "MM", "MM", "DD", "DD", "HH24", "HH12", "MI", "SS" });
        }

        private static string TranslateDotNetDateFormatPattern(string format, string targetProvider)
        {
            string translated = format ?? "";
            if (targetProvider == "mssql") return translated;

            if (targetProvider == "mysql" || targetProvider == "sqlite")
            {
                return ReplaceDateFormatTokens(
                    translated,
                    new[] { "yyyy", "yy", "MM", "M", "dd", "d", "HH", "hh", "mm", "ss" },
                    new[] { "%Y", "%y", "%m", "%c", "%d", "%e", "%H", "%h", "%i", "%s" });
            }

            return ReplaceDateFormatTokens(
                translated,
                new[] { "yyyy", "yy", "MM", "M", "dd", "d", "HH", "hh", "mm", "ss" },
                new[] { "YYYY", "YY", "MM", "MM", "DD", "DD", "HH24", "HH12", "MI", "SS" });
        }

        private static string TranslateOracleDateFormatPattern(string format, string targetProvider)
        {
            string translated = format ?? "";
            if (targetProvider == "oracle" || targetProvider == "postgresql") return translated;

            if (targetProvider == "mysql" || targetProvider == "sqlite")
            {
                return ReplaceDateFormatTokens(
                    translated,
                    new[] { "YYYY", "HH24", "HH12", "YY", "MM", "DD", "MI", "SS" },
                    new[] { "%Y", "%H", "%h", "%y", "%m", "%d", "%i", "%s" });
            }

            return ReplaceDateFormatTokens(
                translated,
                new[] { "YYYY", "HH24", "HH12", "YY", "MM", "DD", "MI", "SS" },
                new[] { "yyyy", "HH", "hh", "yy", "MM", "dd", "mm", "ss" });
        }

        private static string ReplaceDateFormatTokens(string pattern, string[] sourceTokens, string[] targetTokens)
        {
            if (string.IsNullOrEmpty(pattern)) return "";

            StringBuilder builder = new StringBuilder(pattern.Length);
            int index = 0;
            while (index < pattern.Length)
            {
                bool matched = false;
                for (int i = 0; i < sourceTokens.Length; i++)
                {
                    string sourceToken = sourceTokens[i];
                    if (index + sourceToken.Length <= pattern.Length &&
                        string.CompareOrdinal(pattern, index, sourceToken, 0, sourceToken.Length) == 0)
                    {
                        builder.Append(targetTokens[i]);
                        index += sourceToken.Length;
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    builder.Append(pattern[index]);
                    index++;
                }
            }

            return builder.ToString();
        }

        private static string EscapeSqlString(string value)
        {
            return (value ?? "").Replace("'", "''");
        }

        private static string AppendRowLimit(string selectSql, string targetProvider, string limit)
        {
            string sql = selectSql.Trim().TrimEnd(';').Trim();
            if (Regex.IsMatch(sql, @"\b(TOP|LIMIT|FETCH\s+FIRST)\b", RegexOptions.IgnoreCase)) return sql;
            if (targetProvider == "mssql") return InsertSqlServerTop(sql, limit);
            if (targetProvider == "oracle") return sql + " FETCH FIRST " + limit + " ROWS ONLY";
            return sql + " LIMIT " + limit;
        }

        private static string InsertSqlServerTop(string selectSql, string limit)
        {
            if (Regex.IsMatch(selectSql, @"^\s*SELECT\s+(DISTINCT\s+)?TOP\b", RegexOptions.IgnoreCase)) return selectSql;
            return Regex.Replace(
                selectSql,
                @"^\s*SELECT\s+(?<distinct>DISTINCT\s+)?",
                m => "SELECT " + m.Groups["distinct"].Value + "TOP (" + limit + ") ",
                RegexOptions.IgnoreCase);
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
            string featureSql = MaskStringLiterals(selectSql);

            if (!provider.Equals("mssql", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(featureSql, @"\bTOP\s+\(?\d+", RegexOptions.IgnoreCase))
            {
                reason = "TOP 語法不是目標資料庫通用語法";
                return true;
            }

            if (!provider.Equals("oracle", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(featureSql, @"\b(CONNECT\s+BY|START\s+WITH|ROWNUM)\b", RegexOptions.IgnoreCase))
            {
                reason = "Oracle 階層查詢或 ROWNUM 無法自動轉換";
                return true;
            }

            if (!provider.Equals("mysql", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(featureSql, @"\bSQL\s+SECURITY\b|@", RegexOptions.IgnoreCase))
            {
                reason = "MySQL 專用 View 語法無法自動轉換";
                return true;
            }

            return false;
        }

        private static string MaskStringLiterals(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return "";

            StringBuilder builder = new StringBuilder(sql.Length);
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            for (int i = 0; i < sql.Length; i++)
            {
                char ch = sql[i];
                if (ch == '\'' && !inDoubleQuote)
                {
                    builder.Append(' ');
                    if (inSingleQuote && i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        builder.Append(' ');
                        i++;
                    }
                    else
                    {
                        inSingleQuote = !inSingleQuote;
                    }
                    continue;
                }

                if (ch == '"' && !inSingleQuote)
                {
                    builder.Append(' ');
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                builder.Append(inSingleQuote || inDoubleQuote ? ' ' : ch);
            }

            return builder.ToString();
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
