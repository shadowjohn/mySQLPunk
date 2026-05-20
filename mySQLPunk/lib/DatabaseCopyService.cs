using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
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
        private sealed class JsonTableColumn
        {
            public string Name { get; set; }
            public string SqlType { get; set; }
            public string JsonPath { get; set; }
        }

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
            if (!TryRewriteOffsetFetchClause(rewrittenSql, provider, out rewrittenSql, out reason))
                return false;
            if (!TryRewriteFetchFirstClause(rewrittenSql, provider, out rewrittenSql, out reason))
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

        private static bool TryRewriteOffsetFetchClause(string selectSql, string targetProvider, out string rewrittenSql, out string reason)
        {
            rewrittenSql = selectSql;
            reason = "";

            Match match = Regex.Match(
                selectSql,
                @"\s+OFFSET\s+(?<offset>\d+)\s+ROWS\s+FETCH\s+(?:NEXT|FIRST)\s+(?<limit>\d+)\s+ROWS\s+ONLY\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return true;
            if (targetProvider == "mssql" || targetProvider == "oracle") return true;

            string body = selectSql.Substring(0, match.Index).Trim();
            string offset = match.Groups["offset"].Value;
            string limit = match.Groups["limit"].Value;
            rewrittenSql = body + " LIMIT " + limit + " OFFSET " + offset;
            return true;
        }

        private static bool TryRewriteFetchFirstClause(string selectSql, string targetProvider, out string rewrittenSql, out string reason)
        {
            rewrittenSql = selectSql;
            reason = "";
            if (Regex.IsMatch(
                selectSql,
                @"\bOFFSET\s+\d+\s+ROWS\s+FETCH\s+(?:NEXT|FIRST)\s+\d+\s+ROWS\s+ONLY\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                return true;
            }

            Match match = Regex.Match(
                selectSql,
                @"\s+FETCH\s+(?:FIRST|NEXT)\s+(?<limit>\d+)\s+ROWS\s+ONLY\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return true;
            if (targetProvider == "oracle") return true;

            string body = selectSql.Substring(0, match.Index).Trim();
            string limit = match.Groups["limit"].Value;
            rewrittenSql = AppendRowLimit(body, targetProvider, limit);
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

            sql = RewriteNullHandlingFunctions(sql, targetProvider);
            sql = RewriteCurrentDateTimeFunctions(sql, targetProvider);
            sql = RewriteDateOnlyFunctions(sql, sourceProvider, targetProvider);
            sql = RewriteDateTruncFunctions(sql, targetProvider);
            sql = RewriteDateFormatFunctions(sql, targetProvider);
            sql = RewriteDateParseFunctions(sql, targetProvider);
            sql = RewriteDateDiffFunctions(sql, targetProvider);
            sql = RewriteDateAddFunctions(sql, targetProvider);
            sql = RewriteEndOfMonthFunctions(sql, targetProvider);
            sql = RewriteDatePartFunctions(sql, targetProvider);
            sql = RewriteDateFromPartsFunctions(sql, targetProvider);
            sql = RewriteConditionalFunctions(sql, targetProvider);
            sql = RewriteNumericFunctions(sql, targetProvider);
            sql = RewriteComparisonFunctions(sql, targetProvider);
            sql = RewriteBooleanLiterals(sql, targetProvider);
            sql = RewriteSqlServerTryCastFunctions(sql, sourceProvider, targetProvider);
            sql = RewritePostgreSqlCastOperators(sql, sourceProvider, targetProvider);
            sql = RewriteNullOrderingClauses(sql, targetProvider);
            sql = RewriteRandomFunctions(sql, sourceProvider, targetProvider);
            sql = RewriteConcatOperators(sql, sourceProvider, targetProvider);
            sql = RewriteConcatFunctions(sql, targetProvider);
            sql = RewriteStringLengthFunctions(sql, targetProvider);
            sql = RewriteTrimFunctions(sql, targetProvider);
            sql = RewriteSubstringFunctions(sql, targetProvider);
            sql = RewriteEdgeSubstringFunctions(sql, targetProvider);
            sql = RewritePaddingFunctions(sql, targetProvider);
            sql = RewriteStringPositionFunctions(sql, targetProvider);
            sql = RewriteStringAggregateFunctions(sql, targetProvider);
            sql = RewritePatternMatchOperators(sql, targetProvider);
            sql = RewriteJsonTableExpressions(sql, targetProvider);
            sql = RewritePostgreSqlJsonOperators(sql, targetProvider);
            sql = RewriteJsonExistsFunctions(sql, targetProvider);
            sql = RewriteJsonLengthFunctions(sql, targetProvider);
            sql = RewriteJsonValueFunctions(sql, targetProvider);
            sql = RewriteJsonQueryFunctions(sql, targetProvider);
            sql = RewriteJsonExtractFunctions(sql, targetProvider);

            return sql;
        }

        private static string RewriteNumericFunctions(string selectSql, string targetProvider)
        {
            string sql = RewriteOracleToNumberFunctions(selectSql, targetProvider);
            sql = RewritePowerFunctions(sql, targetProvider);
            if (targetProvider == "mssql")
            {
                sql = Regex.Replace(
                    sql,
                    @"\bCEIL\s*\(",
                    "CEILING(",
                    RegexOptions.IgnoreCase);
                return Regex.Replace(
                    sql,
                    @"\bMOD\s*\((?<args>[^()]*)\)",
                    m => RewriteModFunction(m),
                    RegexOptions.IgnoreCase);
            }

            return Regex.Replace(
                sql,
                @"\bCEILING\s*\(",
                "CEIL(",
                RegexOptions.IgnoreCase);
        }

        private static string RewriteOracleToNumberFunctions(string selectSql, string targetProvider)
        {
            if (targetProvider == "oracle") return selectSql;

            return Regex.Replace(
                selectSql,
                @"\bTO_NUMBER\s*\((?<args>[^()]*)\)",
                m => RewriteOracleToNumberFunction(m, targetProvider),
                RegexOptions.IgnoreCase);
        }

        private static string RewriteOracleToNumberFunction(Match match, string targetProvider)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count == 0 || args.Count > 2) return match.Value;

            string targetType = GetGenericNumericCastType(targetProvider);
            if (string.IsNullOrWhiteSpace(targetType)) return match.Value;

            return "CAST(" + args[0] + " AS " + targetType + ")";
        }

        private static string GetGenericNumericCastType(string targetProvider)
        {
            if (targetProvider == "sqlite") return "NUMERIC";
            if (targetProvider == "postgresql") return "numeric";
            if (targetProvider == "mysql") return "DECIMAL(18,4)";
            if (targetProvider == "mssql") return "decimal(18,4)";
            return "";
        }

        private static string RewritePowerFunctions(string selectSql, string targetProvider)
        {
            if (targetProvider == "mysql" || targetProvider == "sqlite") return selectSql;

            return Regex.Replace(
                selectSql,
                @"\bPOW\s*\(",
                "POWER(",
                RegexOptions.IgnoreCase);
        }

        private static string RewriteComparisonFunctions(string selectSql, string targetProvider)
        {
            if (targetProvider != "mssql") return selectSql;

            string sql = Regex.Replace(
                selectSql,
                @"\bGREATEST\s*\((?<args>[^()]*)\)",
                m => RewriteGreatestLeastFunction(m, true),
                RegexOptions.IgnoreCase);

            return Regex.Replace(
                sql,
                @"\bLEAST\s*\((?<args>[^()]*)\)",
                m => RewriteGreatestLeastFunction(m, false),
                RegexOptions.IgnoreCase);
        }

        private static string RewriteGreatestLeastFunction(Match match, bool greatest)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count != 2) return match.Value;

            string comparison = greatest ? ">=" : "<=";
            return "(CASE WHEN " + args[0] + " " + comparison + " " + args[1] + " THEN " + args[0] + " ELSE " + args[1] + " END)";
        }

        private static string RewriteBooleanLiterals(string selectSql, string targetProvider)
        {
            if (targetProvider != "mssql" && targetProvider != "oracle") return selectSql;

            return ReplaceOutsideSingleQuotedStrings(selectSql, segment =>
            {
                string sql = Regex.Replace(
                    segment,
                    @"(?<![""`\[])\bTRUE\b(?![""`\]])",
                    "1",
                    RegexOptions.IgnoreCase);

                return Regex.Replace(
                    sql,
                    @"(?<![""`\[])\bFALSE\b(?![""`\]])",
                    "0",
                    RegexOptions.IgnoreCase);
            });
        }

        private static string RewritePostgreSqlCastOperators(string selectSql, string sourceProvider, string targetProvider)
        {
            if (sourceProvider != "postgresql" || targetProvider == "postgresql") return selectSql;

            return ReplaceOutsideSingleQuotedStrings(selectSql, segment =>
            {
                return Regex.Replace(
                    segment,
                    @"(?<expr>\b[A-Za-z_][A-Za-z0-9_\.]*\b|\([^)]+\))\s*::\s*(?<type>character\s+varying|varchar|char|text|integer|int|bigint|numeric|decimal|date|timestamptz|timestamp|datetime|boolean|bool)(?:\s*\(\s*(?<precision>\d+)(?:\s*,\s*(?<scale>\d+))?\s*\))?",
                    m => RewritePostgreSqlCastOperator(m, targetProvider),
                    RegexOptions.IgnoreCase);
            });
        }

        private static string RewritePostgreSqlCastOperator(Match match, string targetProvider)
        {
            string targetType = MapPostgreSqlCastType(
                match.Groups["type"].Value,
                match.Groups["precision"].Success ? match.Groups["precision"].Value : "",
                match.Groups["scale"].Success ? match.Groups["scale"].Value : "",
                targetProvider);
            if (string.IsNullOrWhiteSpace(targetType)) return match.Value;

            return "CAST(" + match.Groups["expr"].Value.Trim() + " AS " + targetType + ")";
        }

        private static string RewriteSqlServerTryCastFunctions(string selectSql, string sourceProvider, string targetProvider)
        {
            if (sourceProvider != "mssql" || targetProvider == "mssql") return selectSql;

            string sql = Regex.Replace(
                selectSql,
                @"\bTRY_CAST\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s+AS\s+(?<type>N?VARCHAR|N?CHAR|VARCHAR|CHAR|TEXT|INT|INTEGER|BIGINT|BIT|NUMERIC|DECIMAL|FLOAT|REAL|DATE|DATETIME2?|SMALLDATETIME)(?:\s*\(\s*(?<precision>\d+)(?:\s*,\s*(?<scale>\d+))?\s*\))?\s*\)",
                m => RewriteSqlServerTryCastFunction(m, targetProvider),
                RegexOptions.IgnoreCase);

            return Regex.Replace(
                sql,
                @"\bTRY_CONVERT\s*\(\s*(?<type>N?VARCHAR|N?CHAR|VARCHAR|CHAR|TEXT|INT|INTEGER|BIGINT|BIT|NUMERIC|DECIMAL|FLOAT|REAL|DATE|DATETIME2?|SMALLDATETIME)(?:\s*\(\s*(?<precision>\d+)(?:\s*,\s*(?<scale>\d+))?\s*\))?\s*,\s*(?<expr>[^,()]+(?:\([^)]*\))?)(?:\s*,\s*(?<style>23|120))?\s*\)",
                m => RewriteSqlServerTryConvertFunction(m, targetProvider),
                RegexOptions.IgnoreCase);
        }

        private static string RewriteSqlServerTryCastFunction(Match match, string targetProvider)
        {
            string targetType = MapSqlServerCastType(
                match.Groups["type"].Value,
                match.Groups["precision"].Success ? match.Groups["precision"].Value : "",
                match.Groups["scale"].Success ? match.Groups["scale"].Value : "",
                targetProvider);
            if (string.IsNullOrWhiteSpace(targetType)) return match.Value;

            return "CAST(" + match.Groups["expr"].Value.Trim() + " AS " + targetType + ")";
        }

        private static string RewriteSqlServerTryConvertFunction(Match match, string targetProvider)
        {
            string sqlType = match.Groups["type"].Value;
            string expr = match.Groups["expr"].Value.Trim();
            string style = match.Groups["style"].Success ? match.Groups["style"].Value : "";
            string normalizedType = Regex.Replace((sqlType ?? "").Trim().ToLowerInvariant(), @"\s+", "");

            if ((normalizedType == "date" || normalizedType == "datetime" || normalizedType == "datetime2" || normalizedType == "smalldatetime") &&
                (style == "23" || style == "120"))
            {
                string pattern = GetSqlServerConvertDateFormat(style);
                if (!string.IsNullOrWhiteSpace(pattern))
                {
                    return BuildDateParseExpression(targetProvider, expr, TranslateDotNetDateFormatPattern(pattern, targetProvider), normalizedType != "date");
                }
            }

            string targetType = MapSqlServerCastType(
                sqlType,
                match.Groups["precision"].Success ? match.Groups["precision"].Value : "",
                match.Groups["scale"].Success ? match.Groups["scale"].Value : "",
                targetProvider);
            if (string.IsNullOrWhiteSpace(targetType)) return match.Value;

            return "CAST(" + expr + " AS " + targetType + ")";
        }

        private static string MapSqlServerCastType(string sqlType, string precision, string scale, string targetProvider)
        {
            string type = Regex.Replace((sqlType ?? "").Trim().ToLowerInvariant(), @"\s+", "");
            if (type == "nvarchar" || type == "varchar" || type == "nchar" || type == "char" || type == "text")
            {
                if (targetProvider == "mysql") return string.IsNullOrWhiteSpace(precision) ? "CHAR" : "CHAR(" + precision + ")";
                if (targetProvider == "oracle") return string.IsNullOrWhiteSpace(precision) ? "VARCHAR2(4000)" : "VARCHAR2(" + precision + ")";
                if (targetProvider == "postgresql" || targetProvider == "sqlite") return "TEXT";
            }

            if (type == "int" || type == "integer")
            {
                if (targetProvider == "mysql") return "SIGNED";
                if (targetProvider == "oracle") return "NUMBER(10)";
                return "INTEGER";
            }

            if (type == "bigint")
            {
                if (targetProvider == "mysql") return "SIGNED";
                if (targetProvider == "oracle") return "NUMBER(19)";
                return "BIGINT";
            }

            if (type == "bit")
            {
                if (targetProvider == "mysql") return "UNSIGNED";
                if (targetProvider == "oracle") return "NUMBER(1)";
                return "INTEGER";
            }

            if (type == "numeric" || type == "decimal")
            {
                bool hasPrecision = !string.IsNullOrWhiteSpace(precision);
                string suffix = hasPrecision ? "(" + precision + (string.IsNullOrWhiteSpace(scale) ? "" : "," + scale) + ")" : "";
                if (targetProvider == "oracle") return hasPrecision ? "NUMBER" + suffix : "NUMBER";
                if (targetProvider == "sqlite") return "NUMERIC";
                return hasPrecision ? "decimal" + suffix : "decimal(18,4)";
            }

            if (type == "float" || type == "real")
            {
                if (targetProvider == "oracle") return "BINARY_DOUBLE";
                if (targetProvider == "sqlite") return "REAL";
                if (targetProvider == "postgresql") return "double precision";
                return "DOUBLE";
            }

            if (type == "date") return targetProvider == "sqlite" ? "TEXT" : "date";
            if (type == "datetime" || type == "datetime2" || type == "smalldatetime")
            {
                if (targetProvider == "mysql") return "DATETIME";
                if (targetProvider == "oracle") return "TIMESTAMP";
                return targetProvider == "sqlite" ? "TEXT" : "timestamp";
            }

            return "";
        }

        private static string MapPostgreSqlCastType(string pgType, string precision, string scale, string targetProvider)
        {
            string type = Regex.Replace((pgType ?? "").Trim().ToLowerInvariant(), @"\s+", " ");
            bool hasPrecision = !string.IsNullOrWhiteSpace(precision);
            string numericSuffix = hasPrecision ? "(" + precision + (string.IsNullOrWhiteSpace(scale) ? "" : "," + scale) + ")" : "";

            if (type == "text" || type == "varchar" || type == "character varying" || type == "char")
            {
                if (targetProvider == "mssql") return hasPrecision ? "nvarchar(" + precision + ")" : "nvarchar(max)";
                if (targetProvider == "mysql") return hasPrecision ? "CHAR(" + precision + ")" : "CHAR";
                if (targetProvider == "oracle") return hasPrecision ? "VARCHAR2(" + precision + ")" : "VARCHAR2(4000)";
                return "TEXT";
            }

            if (type == "integer" || type == "int")
            {
                if (targetProvider == "mysql") return "SIGNED";
                if (targetProvider == "oracle") return "NUMBER(10)";
                return "INTEGER";
            }

            if (type == "bigint")
            {
                if (targetProvider == "mysql") return "SIGNED";
                if (targetProvider == "oracle") return "NUMBER(19)";
                return "BIGINT";
            }

            if (type == "numeric" || type == "decimal")
            {
                if (targetProvider == "oracle") return hasPrecision ? "NUMBER" + numericSuffix : "NUMBER";
                if (targetProvider == "sqlite") return "NUMERIC";
                return hasPrecision ? "decimal" + numericSuffix : "decimal(18,4)";
            }

            if (type == "date")
            {
                if (targetProvider == "sqlite") return "TEXT";
                return "date";
            }

            if (type == "timestamp" || type == "timestamptz" || type == "datetime")
            {
                if (targetProvider == "mssql") return "datetime2";
                if (targetProvider == "mysql") return "DATETIME";
                if (targetProvider == "oracle") return "TIMESTAMP";
                return "TEXT";
            }

            if (type == "boolean" || type == "bool")
            {
                if (targetProvider == "mssql") return "bit";
                if (targetProvider == "mysql") return "UNSIGNED";
                if (targetProvider == "oracle") return "NUMBER(1)";
                return "INTEGER";
            }

            return "";
        }

        private static string RewriteNullOrderingClauses(string selectSql, string targetProvider)
        {
            if (targetProvider != "mssql" && targetProvider != "mysql") return selectSql;

            return ReplaceOutsideSingleQuotedStrings(selectSql, segment =>
            {
                return Regex.Replace(
                    segment,
                    @"(?<expr>\b[A-Za-z_][A-Za-z0-9_\.]*\b|`[^`]+`|\[[^\]]+\]|""[^""]+"")\s*(?<direction>ASC|DESC)?\s+NULLS\s+(?<placement>FIRST|LAST)",
                    m => RewriteNullOrderingClause(m),
                    RegexOptions.IgnoreCase);
            });
        }

        private static string RewriteNullOrderingClause(Match match)
        {
            string expr = match.Groups["expr"].Value.Trim();
            string direction = match.Groups["direction"].Success
                ? match.Groups["direction"].Value.ToUpperInvariant()
                : "ASC";
            bool nullsFirst = string.Equals(match.Groups["placement"].Value, "FIRST", StringComparison.OrdinalIgnoreCase);
            string nullRank = nullsFirst ? "0" : "1";
            string valueRank = nullsFirst ? "1" : "0";

            return "CASE WHEN " + expr + " IS NULL THEN " + nullRank + " ELSE " + valueRank + " END, " + expr + " " + direction;
        }

        private static string RewriteRandomFunctions(string selectSql, string sourceProvider, string targetProvider)
        {
            if (string.Equals(sourceProvider, targetProvider, StringComparison.OrdinalIgnoreCase)) return selectSql;

            string sql = Regex.Replace(
                selectSql,
                @"\bDBMS_RANDOM\s*\.\s*VALUE\s*(?:\(\s*\))?",
                m => BuildRandomExpression(targetProvider),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bRAND\s*\(\s*\)",
                m => BuildRandomExpression(targetProvider),
                RegexOptions.IgnoreCase);

            return Regex.Replace(
                sql,
                @"\bRANDOM\s*\(\s*\)",
                m => BuildRandomExpression(targetProvider),
                RegexOptions.IgnoreCase);
        }

        private static string BuildRandomExpression(string targetProvider)
        {
            if (targetProvider == "mssql" || targetProvider == "mysql") return "RAND()";
            if (targetProvider == "oracle") return "DBMS_RANDOM.VALUE";
            if (targetProvider == "sqlite") return "((RANDOM() + 9223372036854775808.0) / 18446744073709551616.0)";
            return "RANDOM()";
        }

        private static string RewriteModFunction(Match match)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count != 2) return match.Value;
            return "(" + args[0] + " % " + args[1] + ")";
        }

        private static string RewriteNullHandlingFunctions(string selectSql, string targetProvider)
        {
            return Regex.Replace(
                selectSql,
                @"\bNVL2\s*\((?<args>[^()]*)\)",
                m => RewriteNvl2Function(m, targetProvider),
                RegexOptions.IgnoreCase);
        }

        private static string RewriteNvl2Function(Match match, string targetProvider)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count != 3) return match.Value;

            string expr = args[0];
            string whenNotNull = args[1];
            string whenNull = args[2];
            if (targetProvider == "oracle") return "NVL2(" + expr + ", " + whenNotNull + ", " + whenNull + ")";
            if (targetProvider == "mysql") return "IF(" + expr + " IS NOT NULL, " + whenNotNull + ", " + whenNull + ")";
            if (targetProvider == "mssql") return "IIF(" + expr + " IS NOT NULL, " + whenNotNull + ", " + whenNull + ")";
            return "CASE WHEN " + expr + " IS NOT NULL THEN " + whenNotNull + " ELSE " + whenNull + " END";
        }

        private static string RewriteDateOnlyFunctions(string selectSql, string sourceProvider, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bDATE\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => BuildDateOnlyExpression(targetProvider, m.Groups["expr"].Value.Trim()),
                RegexOptions.IgnoreCase);

            if (sourceProvider == "oracle")
            {
                sql = Regex.Replace(
                    sql,
                    @"\bTRUNC\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                    m => BuildDateOnlyExpression(targetProvider, m.Groups["expr"].Value.Trim()),
                    RegexOptions.IgnoreCase);
            }

            return sql;
        }

        private static string BuildDateOnlyExpression(string targetProvider, string expr)
        {
            if (targetProvider == "mssql" || targetProvider == "postgresql") return "CAST(" + expr + " AS date)";
            if (targetProvider == "oracle") return "TRUNC(" + expr + ")";
            return "DATE(" + expr + ")";
        }

        private static string RewriteDateTruncFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bDATE_TRUNC\s*\(\s*'(?<part>year|month|day|hour|minute|second)'\s*,\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => BuildDateTruncExpression(targetProvider, NormalizeDatePart(m.Groups["part"].Value), m.Groups["expr"].Value.Trim()),
                RegexOptions.IgnoreCase);

            return Regex.Replace(
                sql,
                @"\bTRUNC\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<part>YYYY|YEAR|MM|MONTH|DD|DAY|HH24|HH|MI|MINUTE)'\s*\)",
                m => BuildDateTruncExpression(targetProvider, NormalizeDateTruncPart(m.Groups["part"].Value), m.Groups["expr"].Value.Trim()),
                RegexOptions.IgnoreCase);
        }

        private static string BuildDateTruncExpression(string targetProvider, string part, string expr)
        {
            if (part == "day") return BuildDateOnlyExpression(targetProvider, expr);

            if (targetProvider == "postgresql") return "DATE_TRUNC('" + part + "', " + expr + ")";

            if (targetProvider == "oracle")
            {
                if (part == "year") return "TRUNC(" + expr + ", 'YYYY')";
                if (part == "month") return "TRUNC(" + expr + ", 'MM')";
                if (part == "hour") return "TRUNC(" + expr + ", 'HH24')";
                if (part == "minute") return "TRUNC(" + expr + ", 'MI')";
                if (part == "second") return "CAST(" + expr + " AS TIMESTAMP(0))";
            }

            if (targetProvider == "mssql")
            {
                if (part == "year") return "DATEFROMPARTS(YEAR(" + expr + "), 1, 1)";
                if (part == "month") return "DATEFROMPARTS(YEAR(" + expr + "), MONTH(" + expr + "), 1)";
                if (part == "hour") return "DATEADD(hour, DATEDIFF(hour, 0, " + expr + "), 0)";
                if (part == "minute") return "DATEADD(minute, DATEDIFF(minute, 0, " + expr + "), 0)";
                if (part == "second") return "DATEADD(second, DATEDIFF(second, 0, " + expr + "), 0)";
            }

            if (targetProvider == "mysql")
            {
                if (part == "year") return "STR_TO_DATE(DATE_FORMAT(" + expr + ", '%Y-01-01'), '%Y-%m-%d')";
                if (part == "month") return "STR_TO_DATE(DATE_FORMAT(" + expr + ", '%Y-%m-01'), '%Y-%m-%d')";
                if (part == "hour") return "STR_TO_DATE(DATE_FORMAT(" + expr + ", '%Y-%m-%d %H:00:00'), '%Y-%m-%d %H:%i:%s')";
                if (part == "minute") return "STR_TO_DATE(DATE_FORMAT(" + expr + ", '%Y-%m-%d %H:%i:00'), '%Y-%m-%d %H:%i:%s')";
                if (part == "second") return "STR_TO_DATE(DATE_FORMAT(" + expr + ", '%Y-%m-%d %H:%i:%s'), '%Y-%m-%d %H:%i:%s')";
            }

            if (targetProvider == "sqlite")
            {
                if (part == "year") return "strftime('%Y-01-01', " + expr + ")";
                if (part == "month") return "strftime('%Y-%m-01', " + expr + ")";
                if (part == "hour") return "strftime('%Y-%m-%d %H:00:00', " + expr + ")";
                if (part == "minute") return "strftime('%Y-%m-%d %H:%M:00', " + expr + ")";
                if (part == "second") return "strftime('%Y-%m-%d %H:%M:%S', " + expr + ")";
            }

            return "DATE_TRUNC('" + part + "', " + expr + ")";
        }

        private static string RewriteEndOfMonthFunctions(string selectSql, string targetProvider)
        {
            string sql = selectSql;
            if (targetProvider != "mssql")
            {
                sql = Regex.Replace(
                    sql,
                    @"\bEOMONTH\s*\((?<args>[^()]*)\)",
                    m => RewriteEndOfMonthFunction(m, targetProvider),
                    RegexOptions.IgnoreCase);
            }

            if (targetProvider != "mysql" && targetProvider != "oracle")
            {
                sql = Regex.Replace(
                    sql,
                    @"\bLAST_DAY\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                    m => BuildLastDayExpression(targetProvider, m.Groups["expr"].Value.Trim()),
                    RegexOptions.IgnoreCase);
            }

            return sql;
        }

        private static string RewriteEndOfMonthFunction(Match match, string targetProvider)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count < 1 || args.Count > 2) return match.Value;

            string expr = args[0];
            string offset = args.Count == 2 ? args[1] : null;

            if (targetProvider == "mysql")
            {
                if (offset == null) return "LAST_DAY(" + expr + ")";
                return "LAST_DAY(DATE_ADD(" + expr + ", INTERVAL " + offset + " MONTH))";
            }

            if (targetProvider == "postgresql")
            {
                string adjustedExpr = offset == null
                    ? expr
                    : "(" + expr + " + (" + offset + " * INTERVAL '1 month'))";
                return "(DATE_TRUNC('month', " + adjustedExpr + ") + INTERVAL '1 month - 1 day')::date";
            }

            if (targetProvider == "sqlite")
            {
                if (offset == null) return "date(" + expr + ", 'start of month', '+1 month', '-1 day')";
                return "date(" + expr + ", printf('%+d month', " + offset + "), 'start of month', '+1 month', '-1 day')";
            }

            if (targetProvider == "oracle")
            {
                if (offset == null) return "LAST_DAY(" + expr + ")";
                return "LAST_DAY(ADD_MONTHS(" + expr + ", " + offset + "))";
            }

            return match.Value;
        }

        private static string BuildLastDayExpression(string targetProvider, string expr)
        {
            if (targetProvider == "mssql") return "EOMONTH(" + expr + ")";
            if (targetProvider == "postgresql") return "(DATE_TRUNC('month', " + expr + ") + INTERVAL '1 month - 1 day')::date";
            if (targetProvider == "sqlite") return "date(" + expr + ", 'start of month', '+1 month', '-1 day')";
            return "LAST_DAY(" + expr + ")";
        }

        private static string RewriteDatePartFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bDATEPART\s*\(\s*(?<part>year|yy|yyyy|month|mm|m|day|dd|d|hour|hh|minute|mi|n|second|ss|s)\s*,\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => BuildDatePartExpression(targetProvider, NormalizeDatePart(m.Groups["part"].Value), m.Groups["expr"].Value.Trim()),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bDATE_PART\s*\(\s*'(?<part>year|month|day|hour|minute|second)'\s*,\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => BuildDatePartExpression(targetProvider, NormalizeDatePart(m.Groups["part"].Value), m.Groups["expr"].Value.Trim()),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bEXTRACT\s*\(\s*(?<part>YEAR|MONTH|DAY|HOUR|MINUTE|SECOND)\s+FROM\s+(?<expr>[^()]+?)\s*\)",
                m => BuildDatePartExpression(targetProvider, NormalizeDatePart(m.Groups["part"].Value), m.Groups["expr"].Value.Trim()),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\b(?<func>YEAR|MONTH|DAY|HOUR|MINUTE|SECOND)\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => BuildDatePartExpression(targetProvider, NormalizeDatePart(m.Groups["func"].Value), m.Groups["expr"].Value.Trim()),
                RegexOptions.IgnoreCase);

            return sql;
        }

        private static string BuildDatePartExpression(string targetProvider, string part, string expr)
        {
            if (targetProvider == "mssql")
            {
                if (part == "year" || part == "month" || part == "day")
                {
                    return part.ToUpperInvariant() + "(" + expr + ")";
                }

                return "DATEPART(" + part + ", " + expr + ")";
            }

            if (targetProvider == "mysql")
            {
                return part.ToUpperInvariant() + "(" + expr + ")";
            }

            if (targetProvider == "sqlite")
            {
                return "CAST(strftime('" + GetSqliteDatePartFormat(part) + "', " + expr + ") AS INTEGER)";
            }

            return "EXTRACT(" + part.ToUpperInvariant() + " FROM " + expr + ")";
        }

        private static string RewriteDateFromPartsFunctions(string selectSql, string targetProvider)
        {
            if (targetProvider == "mssql") return selectSql;

            return Regex.Replace(
                selectSql,
                @"\bDATEFROMPARTS\s*\((?<args>[^()]*)\)",
                m => RewriteDateFromPartsFunction(m, targetProvider),
                RegexOptions.IgnoreCase);
        }

        private static string RewriteDateFromPartsFunction(Match match, string targetProvider)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count != 3) return match.Value;

            string year = args[0];
            string month = args[1];
            string day = args[2];

            if (targetProvider == "mysql")
            {
                return "STR_TO_DATE(CONCAT(" + year + ", '-', " + month + ", '-', " + day + "), '%Y-%m-%d')";
            }

            if (targetProvider == "postgresql")
            {
                return "MAKE_DATE(" + year + ", " + month + ", " + day + ")";
            }

            if (targetProvider == "sqlite")
            {
                return "printf('%04d-%02d-%02d', " + year + ", " + month + ", " + day + ")";
            }

            if (targetProvider == "oracle")
            {
                return "TO_DATE(" + year + " || '-' || " + month + " || '-' || " + day + ", 'YYYY-MM-DD')";
            }

            return match.Value;
        }

        private static string NormalizeDatePart(string part)
        {
            string text = (part ?? string.Empty).Trim().Trim('\'', '"', '[', ']').ToLowerInvariant();
            if (text == "yy" || text == "yyyy") return "year";
            if (text == "mm" || text == "m") return "month";
            if (text == "dd" || text == "d") return "day";
            if (text == "hh") return "hour";
            if (text == "mi" || text == "n") return "minute";
            if (text == "ss" || text == "s") return "second";
            return text;
        }

        private static string NormalizeDateTruncPart(string part)
        {
            string text = (part ?? string.Empty).Trim().Trim('\'', '"').ToLowerInvariant();
            if (text == "yyyy") return "year";
            if (text == "mm") return "month";
            if (text == "dd") return "day";
            if (text == "hh24" || text == "hh") return "hour";
            if (text == "mi") return "minute";
            return NormalizeDatePart(text);
        }

        private static string GetSqliteDatePartFormat(string part)
        {
            if (part == "year") return "%Y";
            if (part == "month") return "%m";
            if (part == "hour") return "%H";
            if (part == "minute") return "%M";
            if (part == "second") return "%S";
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

            sql = Regex.Replace(
                sql,
                @"\bDECODE\s*\((?<args>[^()]*)\)",
                m => RewriteDecodeFunction(m, targetProvider),
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

        private static string RewriteDecodeFunction(Match match, string targetProvider)
        {
            if (targetProvider == "oracle") return match.Value;

            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count < 3) return match.Value;

            string expr = args[0];
            int pairCount = (args.Count - 1) / 2;
            bool hasDefault = args.Count % 2 == 0;
            StringBuilder builder = new StringBuilder();
            builder.Append("CASE ").Append(expr);
            for (int i = 0; i < pairCount; i++)
            {
                builder.Append(" WHEN ").Append(args[1 + (i * 2)]).Append(" THEN ").Append(args[2 + (i * 2)]);
            }

            if (hasDefault)
            {
                builder.Append(" ELSE ").Append(args[args.Count - 1]);
            }

            builder.Append(" END");
            return builder.ToString();
        }

        private static string RewriteCurrentDateTimeFunctions(string selectSql, string targetProvider)
        {
            return ReplaceOutsideSingleQuotedStrings(selectSql, segment =>
            {
                string sql = segment;
                if (targetProvider != "mssql")
                {
                    sql = Regex.Replace(sql, @"\bGETUTCDATE\s*\(\s*\)", m => BuildUtcTimestampExpression(targetProvider), RegexOptions.IgnoreCase);
                }

                if (targetProvider != "mysql")
                {
                    sql = Regex.Replace(sql, @"\bUTC_TIMESTAMP\s*\(\s*\)", m => BuildUtcTimestampExpression(targetProvider), RegexOptions.IgnoreCase);
                }

                if (targetProvider != "mssql")
                {
                    sql = Regex.Replace(sql, @"\bGETDATE\s*\(\s*\)", "CURRENT_TIMESTAMP", RegexOptions.IgnoreCase);
                }

                if (targetProvider != "mysql")
                {
                    sql = Regex.Replace(sql, @"\bNOW\s*\(\s*\)", "CURRENT_TIMESTAMP", RegexOptions.IgnoreCase);
                }

                if (targetProvider != "oracle")
                {
                    sql = Regex.Replace(
                        sql,
                        @"\b(?:SYSDATE|SYSTIMESTAMP)(?:\s*\(\s*\))?",
                        m => BuildCurrentTimestampExpression(targetProvider),
                        RegexOptions.IgnoreCase);
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
            });
        }

        private static string BuildCurrentDateExpression(string targetProvider)
        {
            if (targetProvider == "mssql") return "CAST(GETDATE() AS date)";
            if (targetProvider == "mysql") return "CURDATE()";
            return "CURRENT_DATE";
        }

        private static string BuildCurrentTimestampExpression(string targetProvider)
        {
            if (targetProvider == "mssql") return "GETDATE()";
            if (targetProvider == "mysql") return "NOW()";
            return "CURRENT_TIMESTAMP";
        }

        private static string BuildUtcTimestampExpression(string targetProvider)
        {
            if (targetProvider == "mssql") return "GETUTCDATE()";
            if (targetProvider == "mysql") return "UTC_TIMESTAMP()";
            if (targetProvider == "oracle") return "SYS_EXTRACT_UTC(SYSTIMESTAMP)";
            if (targetProvider == "sqlite") return "datetime('now')";
            return "CURRENT_TIMESTAMP AT TIME ZONE 'UTC'";
        }

        private static string RewriteDateFormatFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bCONVERT\s*\(\s*(?<type>N?VARCHAR|N?CHAR|VARCHAR|CHAR)\s*(?:\(\s*\d+\s*\))?\s*,\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*(?<style>23|120)\s*\)",
                m =>
                {
                    if (targetProvider == "mssql") return m.Value;
                    string expr = m.Groups["expr"].Value.Trim();
                    string format = GetSqlServerConvertDateFormat(m.Groups["style"].Value);
                    if (string.IsNullOrWhiteSpace(format)) return m.Value;
                    return BuildDateFormatExpression(targetProvider, expr, TranslateDotNetDateFormatPattern(format, targetProvider));
                },
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
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

        private static string RewriteDateParseFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bTO_TIMESTAMP\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<format>[^']+)'\s*\)",
                m =>
                {
                    string expr = m.Groups["expr"].Value.Trim();
                    string format = m.Groups["format"].Value;
                    if (targetProvider == "oracle" || targetProvider == "postgresql") return m.Value;
                    return BuildDateParseExpression(targetProvider, expr, TranslateOracleDateFormatPattern(format, targetProvider), true);
                },
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bTO_DATE\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<format>[^']+)'\s*\)",
                m =>
                {
                    string expr = m.Groups["expr"].Value.Trim();
                    string format = m.Groups["format"].Value;
                    if (targetProvider == "oracle") return m.Value;
                    return BuildDateParseExpression(targetProvider, expr, TranslateOracleDateFormatPattern(format, targetProvider), false);
                },
                RegexOptions.IgnoreCase);

            return Regex.Replace(
                sql,
                @"\bSTR_TO_DATE\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<format>[^']+)'\s*\)",
                m =>
                {
                    string expr = m.Groups["expr"].Value.Trim();
                    string format = m.Groups["format"].Value;
                    if (targetProvider == "mysql") return m.Value;
                    return BuildDateParseExpression(targetProvider, expr, TranslateMySqlDateFormatPattern(format, targetProvider), false);
                },
                RegexOptions.IgnoreCase);
        }

        private static string GetSqlServerConvertDateFormat(string style)
        {
            if (style == "23") return "yyyy-MM-dd";
            if (style == "120") return "yyyy-MM-dd HH:mm:ss";
            return "";
        }

        private static string RewriteDateDiffFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bTIMESTAMPDIFF\s*\(\s*(?<part>YEAR|YY|YYYY|MONTH|MM|M|DAY|DD|D|HOUR|HH|MINUTE|MI|N|SECOND|SS|S)\s*,\s*(?<start>[^,()]+(?:\([^)]*\))?)\s*,\s*(?<end>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => RewriteTimestampDiffFunction(m, targetProvider),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bMONTHS_BETWEEN\s*\((?<args>[^()]*)\)",
                m => RewriteMonthsBetweenFunction(m, targetProvider),
                RegexOptions.IgnoreCase);

            return Regex.Replace(
                sql,
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

            if (args.Count == 3)
            {
                string expression = BuildDateDiffExpression(targetProvider, NormalizeDatePart(args[0]), args[2], args[1]);
                if (!string.IsNullOrWhiteSpace(expression)) return expression;
            }

            return match.Value;
        }

        private static string RewriteTimestampDiffFunction(Match match, string targetProvider)
        {
            string expression = BuildDateDiffExpression(
                targetProvider,
                NormalizeDatePart(match.Groups["part"].Value),
                match.Groups["end"].Value.Trim(),
                match.Groups["start"].Value.Trim());
            return string.IsNullOrWhiteSpace(expression) ? match.Value : expression;
        }

        private static string RewriteMonthsBetweenFunction(Match match, string targetProvider)
        {
            if (targetProvider == "oracle") return match.Value;

            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count != 2) return match.Value;

            return BuildDateDiffExpression(targetProvider, "month", args[0], args[1]);
        }

        private static string RewriteDateAddFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bDATE_ADD\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*INTERVAL\s+(?<amount>-?\d+)\s+(?<part>YEAR|YY|YYYY|MONTH|MM|M|DAY|DD|D|HOUR|HH|MINUTE|MI|N|SECOND|SS|S)\s*\)",
                m => BuildDateAddExpression(targetProvider, m.Groups["expr"].Value.Trim(), m.Groups["amount"].Value, NormalizeDatePart(m.Groups["part"].Value)),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bDATE_SUB\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*INTERVAL\s+(?<amount>-?\d+)\s+(?<part>YEAR|YY|YYYY|MONTH|MM|M|DAY|DD|D|HOUR|HH|MINUTE|MI|N|SECOND|SS|S)\s*\)",
                m => BuildDateAddExpression(targetProvider, m.Groups["expr"].Value.Trim(), NegateIntegerString(m.Groups["amount"].Value), NormalizeDatePart(m.Groups["part"].Value)),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"\bDATEADD\s*\(\s*(?<part>year|yy|yyyy|month|mm|m|day|dd|d|hour|hh|minute|mi|n|second|ss|s)\s*,\s*(?<amount>-?\d+)\s*,\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => BuildDateAddExpression(targetProvider, m.Groups["expr"].Value.Trim(), m.Groups["amount"].Value, NormalizeDatePart(m.Groups["part"].Value)),
                RegexOptions.IgnoreCase);

            return Regex.Replace(
                sql,
                @"\bADD_MONTHS\s*\((?<args>[^()]*)\)",
                m => RewriteAddMonthsFunction(m, targetProvider),
                RegexOptions.IgnoreCase);
        }

        private static string RewriteAddMonthsFunction(Match match, string targetProvider)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count != 2) return match.Value;
            return BuildDateAddExpression(targetProvider, args[0], args[1], "month");
        }

        private static string BuildDateAddExpression(string targetProvider, string expr, string amount, string part)
        {
            if (targetProvider == "mssql") return "DATEADD(" + part + ", " + amount + ", " + expr + ")";
            if (targetProvider == "mysql") return "DATE_ADD(" + expr + ", INTERVAL " + amount + " " + part.ToUpperInvariant() + ")";
            if (targetProvider == "sqlite") return BuildSqliteDateAddExpression(expr, amount, part);
            if (targetProvider == "oracle") return BuildOracleDateAddExpression(expr, amount, part);
            if (!IsIntegerString(amount)) return expr + " + (" + amount + " * INTERVAL '1 " + part + "')";
            return expr + " + INTERVAL '" + amount + " " + part + "'";
        }

        private static string BuildSqliteDateAddExpression(string expr, string amount, string part)
        {
            string functionName = (part == "hour" || part == "minute" || part == "second") ? "datetime" : "date";
            string modifier = amount.StartsWith("-", StringComparison.Ordinal) ? amount : "+" + amount;
            return functionName + "(" + expr + ", '" + modifier + " " + part + "')";
        }

        private static string BuildOracleDateAddExpression(string expr, string amount, string part)
        {
            if (part == "day") return expr + " + " + amount;
            if (part == "month") return "ADD_MONTHS(" + expr + ", " + amount + ")";
            if (part == "year") return "ADD_MONTHS(" + expr + ", " + MultiplyIntegerString(amount, 12) + ")";
            return expr + " + NUMTODSINTERVAL(" + amount + ", '" + part.ToUpperInvariant() + "')";
        }

        private static string MultiplyIntegerString(string value, int multiplier)
        {
            int parsed;
            if (int.TryParse((value ?? string.Empty).Trim(), out parsed))
            {
                return (parsed * multiplier).ToString(CultureInfo.InvariantCulture);
            }

            return "(" + value + " * " + multiplier.ToString(CultureInfo.InvariantCulture) + ")";
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

        private static string BuildDateDiffExpression(string targetProvider, string part, string endDate, string startDate)
        {
            if (part == "day") return BuildDateDiffDaysExpression(targetProvider, endDate, startDate);

            if (targetProvider == "mssql")
            {
                return "DATEDIFF(" + part + ", " + startDate + ", " + endDate + ")";
            }

            if (targetProvider == "mysql")
            {
                return "TIMESTAMPDIFF(" + part.ToUpperInvariant() + ", " + startDate + ", " + endDate + ")";
            }

            if (part == "year" || part == "month")
            {
                string expression = BuildCalendarDateDiffExpression(targetProvider, part, endDate, startDate);
                if (!string.IsNullOrWhiteSpace(expression)) return expression;
            }

            int seconds = GetDateDiffSeconds(part);
            if (seconds <= 0) return "";

            if (targetProvider == "sqlite")
            {
                return "CAST(((julianday(" + endDate + ") - julianday(" + startDate + ")) * 86400) / " + seconds + " AS INTEGER)";
            }

            if (targetProvider == "oracle")
            {
                return "FLOOR(((" + endDate + ") - (" + startDate + ")) * " + (86400 / seconds).ToString(CultureInfo.InvariantCulture) + ")";
            }

            return "CAST(EXTRACT(EPOCH FROM (" + endDate + " - " + startDate + ")) / " + seconds + " AS INTEGER)";
        }

        private static string BuildCalendarDateDiffExpression(string targetProvider, string part, string endDate, string startDate)
        {
            if (targetProvider == "sqlite")
            {
                string yearDiff = "(CAST(strftime('%Y', " + endDate + ") AS INTEGER) - CAST(strftime('%Y', " + startDate + ") AS INTEGER))";
                if (part == "year") return yearDiff;
                return "(" + yearDiff + " * 12 + (CAST(strftime('%m', " + endDate + ") AS INTEGER) - CAST(strftime('%m', " + startDate + ") AS INTEGER)))";
            }

            if (targetProvider == "oracle")
            {
                if (part == "year") return "FLOOR(MONTHS_BETWEEN(" + endDate + ", " + startDate + ") / 12)";
                return "FLOOR(MONTHS_BETWEEN(" + endDate + ", " + startDate + "))";
            }

            string age = "AGE(" + endDate + ", " + startDate + ")";
            if (part == "year") return "CAST(EXTRACT(YEAR FROM " + age + ") AS INTEGER)";
            return "CAST((EXTRACT(YEAR FROM " + age + ") * 12) + EXTRACT(MONTH FROM " + age + ") AS INTEGER)";
        }

        private static int GetDateDiffSeconds(string part)
        {
            if (part == "hour") return 3600;
            if (part == "minute") return 60;
            if (part == "second") return 1;
            return 0;
        }

        private static bool IsIntegerString(string value)
        {
            return Regex.IsMatch((value ?? string.Empty).Trim(), @"^-?\d+$");
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

        private static string RewriteConcatOperators(string selectSql, string sourceProvider, string targetProvider)
        {
            if (targetProvider != "mysql" && targetProvider != "mssql") return selectSql;
            if (sourceProvider == "mysql" || sourceProvider == "mssql") return selectSql;
            if (string.IsNullOrEmpty(selectSql) || selectSql.IndexOf("||", StringComparison.Ordinal) < 0) return selectSql;

            StringBuilder output = new StringBuilder(selectSql.Length);
            int index = 0;
            while (index < selectSql.Length)
            {
                int operatorIndex = FindNextConcatOperator(selectSql, index);
                if (operatorIndex < 0)
                {
                    output.Append(selectSql.Substring(index));
                    break;
                }

                int start = FindConcatOperandStart(selectSql, operatorIndex - 1);
                int end = FindConcatChainEnd(selectSql, operatorIndex + 2);
                if (start < index || end <= operatorIndex + 2)
                {
                    output.Append(selectSql.Substring(index, operatorIndex + 2 - index));
                    index = operatorIndex + 2;
                    continue;
                }

                string chain = selectSql.Substring(start, end - start);
                List<string> operands = SplitConcatOperatorOperands(chain);
                if (operands.Count < 2)
                {
                    output.Append(selectSql.Substring(index, end - index));
                }
                else
                {
                    output.Append(selectSql.Substring(index, start - index));
                    output.Append(BuildConcatOperatorExpression(operands, targetProvider));
                }

                index = end;
            }

            return output.ToString();
        }

        private static string BuildConcatOperatorExpression(List<string> operands, string targetProvider)
        {
            if (targetProvider == "mssql")
            {
                return string.Join(" + ", operands.ToArray());
            }

            return "CONCAT(" + string.Join(", ", operands.ToArray()) + ")";
        }

        private static int FindNextConcatOperator(string sql, int startIndex)
        {
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            bool inBracketQuote = false;
            bool inBacktickQuote = false;

            for (int i = Math.Max(0, startIndex); i < sql.Length - 1; i++)
            {
                char ch = sql[i];
                if (inSingleQuote)
                {
                    if (ch == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        i++;
                    }
                    else if (ch == '\'')
                    {
                        inSingleQuote = false;
                    }
                    continue;
                }

                if (inDoubleQuote)
                {
                    if (ch == '"') inDoubleQuote = false;
                    continue;
                }

                if (inBracketQuote)
                {
                    if (ch == ']') inBracketQuote = false;
                    continue;
                }

                if (inBacktickQuote)
                {
                    if (ch == '`') inBacktickQuote = false;
                    continue;
                }

                if (ch == '\'') { inSingleQuote = true; continue; }
                if (ch == '"') { inDoubleQuote = true; continue; }
                if (ch == '[') { inBracketQuote = true; continue; }
                if (ch == '`') { inBacktickQuote = true; continue; }

                if (ch == '|' && sql[i + 1] == '|') return i;
            }

            return -1;
        }

        private static int FindConcatOperandStart(string sql, int endIndex)
        {
            int index = SkipWhitespaceLeft(sql, endIndex);
            if (index < 0) return 0;

            char ch = sql[index];
            if (ch == '\'') return FindSingleQuotedStringStart(sql, index);
            if (ch == '"') return FindDoubleQuotedIdentifierStart(sql, index);
            if (ch == '`') return FindBacktickIdentifierStart(sql, index);
            if (ch == ']') return FindBracketIdentifierStart(sql, index);

            if (ch == ')')
            {
                int openParen = FindMatchingOpenParen(sql, index);
                if (openParen < 0) return index;
                int functionNameEnd = SkipWhitespaceLeft(sql, openParen - 1);
                int functionNameStart = FindIdentifierStart(sql, functionNameEnd);
                return functionNameStart >= 0 ? functionNameStart : openParen;
            }

            int start = FindIdentifierStart(sql, index);
            return start >= 0 ? start : index;
        }

        private static int FindConcatChainEnd(string sql, int afterOperatorIndex)
        {
            int end = FindConcatOperandEnd(sql, afterOperatorIndex);
            if (end <= afterOperatorIndex) return afterOperatorIndex;

            while (true)
            {
                int next = SkipWhitespaceRight(sql, end);
                if (next + 1 >= sql.Length || sql[next] != '|' || sql[next + 1] != '|') break;

                int nextEnd = FindConcatOperandEnd(sql, next + 2);
                if (nextEnd <= next + 2) break;
                end = nextEnd;
            }

            return end;
        }

        private static int FindConcatOperandEnd(string sql, int startIndex)
        {
            int index = SkipWhitespaceRight(sql, startIndex);
            if (index >= sql.Length) return startIndex;

            char ch = sql[index];
            if (ch == '\'') return FindSingleQuotedStringEnd(sql, index) + 1;
            if (ch == '(')
            {
                int closeParen = FindMatchingCloseParen(sql, index);
                return closeParen >= 0 ? closeParen + 1 : index + 1;
            }

            int end = FindIdentifierChainEnd(sql, index);
            if (end <= index) return index + 1;

            int afterIdentifier = SkipWhitespaceRight(sql, end);
            if (afterIdentifier < sql.Length && sql[afterIdentifier] == '(')
            {
                int closeParen = FindMatchingCloseParen(sql, afterIdentifier);
                if (closeParen >= 0) return closeParen + 1;
            }

            return end;
        }

        private static List<string> SplitConcatOperatorOperands(string chain)
        {
            List<string> operands = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            bool inBracketQuote = false;
            bool inBacktickQuote = false;
            int depth = 0;

            for (int i = 0; i < (chain ?? "").Length; i++)
            {
                char ch = chain[i];
                if (inSingleQuote)
                {
                    current.Append(ch);
                    if (ch == '\'' && i + 1 < chain.Length && chain[i + 1] == '\'')
                    {
                        current.Append(chain[++i]);
                    }
                    else if (ch == '\'')
                    {
                        inSingleQuote = false;
                    }
                    continue;
                }

                if (inDoubleQuote)
                {
                    current.Append(ch);
                    if (ch == '"') inDoubleQuote = false;
                    continue;
                }

                if (inBracketQuote)
                {
                    current.Append(ch);
                    if (ch == ']') inBracketQuote = false;
                    continue;
                }

                if (inBacktickQuote)
                {
                    current.Append(ch);
                    if (ch == '`') inBacktickQuote = false;
                    continue;
                }

                if (ch == '\'') { inSingleQuote = true; current.Append(ch); continue; }
                if (ch == '"') { inDoubleQuote = true; current.Append(ch); continue; }
                if (ch == '[') { inBracketQuote = true; current.Append(ch); continue; }
                if (ch == '`') { inBacktickQuote = true; current.Append(ch); continue; }
                if (ch == '(') { depth++; current.Append(ch); continue; }
                if (ch == ')') { if (depth > 0) depth--; current.Append(ch); continue; }

                if (ch == '|' && i + 1 < chain.Length && chain[i + 1] == '|' && depth == 0)
                {
                    string item = current.ToString().Trim();
                    if (item.Length > 0) operands.Add(item);
                    current.Length = 0;
                    i++;
                    continue;
                }

                current.Append(ch);
            }

            string tail = current.ToString().Trim();
            if (tail.Length > 0) operands.Add(tail);
            return operands;
        }

        private static int SkipWhitespaceLeft(string text, int index)
        {
            int current = Math.Min(index, (text ?? "").Length - 1);
            while (current >= 0 && char.IsWhiteSpace(text[current])) current--;
            return current;
        }

        private static int SkipWhitespaceRight(string text, int index)
        {
            int current = Math.Max(0, index);
            while (current < (text ?? "").Length && char.IsWhiteSpace(text[current])) current++;
            return current;
        }

        private static int FindSingleQuotedStringStart(string text, int endQuoteIndex)
        {
            bool inSingleQuote = false;
            int start = endQuoteIndex;
            for (int i = 0; i <= endQuoteIndex && i < text.Length; i++)
            {
                if (text[i] != '\'') continue;
                if (!inSingleQuote)
                {
                    start = i;
                    inSingleQuote = true;
                    continue;
                }

                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                if (i == endQuoteIndex) return start;
                inSingleQuote = false;
            }

            return start;
        }

        private static int FindSingleQuotedStringEnd(string text, int startQuoteIndex)
        {
            for (int i = startQuoteIndex + 1; i < text.Length; i++)
            {
                if (text[i] != '\'') continue;
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                return i;
            }

            return startQuoteIndex;
        }

        private static int FindDoubleQuotedIdentifierStart(string text, int endQuoteIndex)
        {
            int start = text.LastIndexOf('"', Math.Max(0, endQuoteIndex - 1));
            return start >= 0 ? start : endQuoteIndex;
        }

        private static int FindBacktickIdentifierStart(string text, int endQuoteIndex)
        {
            int start = text.LastIndexOf('`', Math.Max(0, endQuoteIndex - 1));
            return start >= 0 ? start : endQuoteIndex;
        }

        private static int FindBracketIdentifierStart(string text, int endQuoteIndex)
        {
            int start = text.LastIndexOf('[', Math.Max(0, endQuoteIndex - 1));
            return start >= 0 ? start : endQuoteIndex;
        }

        private static int FindMatchingOpenParen(string text, int closeParenIndex)
        {
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            int depth = 0;
            for (int i = closeParenIndex; i >= 0; i--)
            {
                char ch = text[i];
                if (inSingleQuote)
                {
                    if (ch == '\'') inSingleQuote = false;
                    continue;
                }

                if (inDoubleQuote)
                {
                    if (ch == '"') inDoubleQuote = false;
                    continue;
                }

                if (ch == '\'') { inSingleQuote = true; continue; }
                if (ch == '"') { inDoubleQuote = true; continue; }
                if (ch == ')') { depth++; continue; }
                if (ch == '(')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }

            return -1;
        }

        private static int FindMatchingCloseParen(string text, int openParenIndex)
        {
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            int depth = 0;
            for (int i = openParenIndex; i < text.Length; i++)
            {
                char ch = text[i];
                if (inSingleQuote)
                {
                    if (ch == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i++;
                    }
                    else if (ch == '\'')
                    {
                        inSingleQuote = false;
                    }
                    continue;
                }

                if (inDoubleQuote)
                {
                    if (ch == '"') inDoubleQuote = false;
                    continue;
                }

                if (ch == '\'') { inSingleQuote = true; continue; }
                if (ch == '"') { inDoubleQuote = true; continue; }
                if (ch == '(') { depth++; continue; }
                if (ch == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }

            return -1;
        }

        private static int FindIdentifierStart(string text, int endIndex)
        {
            if (string.IsNullOrEmpty(text) || endIndex < 0) return -1;
            int index = Math.Min(endIndex, text.Length - 1);
            while (index >= 0 && IsIdentifierChainChar(text[index])) index--;
            return index + 1 <= endIndex ? index + 1 : -1;
        }

        private static int FindIdentifierChainEnd(string text, int startIndex)
        {
            if (string.IsNullOrEmpty(text) || startIndex >= text.Length) return startIndex;

            int index = startIndex;
            while (index < text.Length)
            {
                char ch = text[index];
                if (ch == '"')
                {
                    index = FindQuotedIdentifierEnd(text, index, '"') + 1;
                }
                else if (ch == '`')
                {
                    index = FindQuotedIdentifierEnd(text, index, '`') + 1;
                }
                else if (ch == '[')
                {
                    int end = text.IndexOf(']', index + 1);
                    index = end >= 0 ? end + 1 : index + 1;
                }
                else if (IsIdentifierChainChar(ch))
                {
                    index++;
                }
                else
                {
                    break;
                }
            }

            return index;
        }

        private static int FindQuotedIdentifierEnd(string text, int startIndex, char quote)
        {
            int end = text.IndexOf(quote, startIndex + 1);
            return end >= 0 ? end : startIndex;
        }

        private static bool IsIdentifierChainChar(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' || ch == '$';
        }

        private static string RewriteStringLengthFunctions(string selectSql, string targetProvider)
        {
            if (targetProvider == "mssql")
            {
                string sql = Regex.Replace(
                    selectSql,
                    @"\b(?:LENGTH|CHAR_LENGTH|CHARACTER_LENGTH)\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                    m => "LEN(" + m.Groups["expr"].Value.Trim() + ")",
                    RegexOptions.IgnoreCase);
                return sql;
            }

            string rewrittenSql = Regex.Replace(
                selectSql,
                @"\bLEN\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                m => "LENGTH(" + m.Groups["expr"].Value.Trim() + ")",
                RegexOptions.IgnoreCase);

            if (targetProvider != "mysql")
            {
                rewrittenSql = Regex.Replace(
                    rewrittenSql,
                    @"\b(?:CHAR_LENGTH|CHARACTER_LENGTH)\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                    m => "LENGTH(" + m.Groups["expr"].Value.Trim() + ")",
                    RegexOptions.IgnoreCase);
            }

            return rewrittenSql;
        }

        private static string RewriteTrimFunctions(string selectSql, string targetProvider)
        {
            string sql = selectSql;
            if (targetProvider == "mssql")
            {
                return Regex.Replace(
                    sql,
                    @"\bTRIM\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)",
                    m => "LTRIM(RTRIM(" + m.Groups["expr"].Value.Trim() + "))",
                    RegexOptions.IgnoreCase);
            }

            sql = Regex.Replace(
                sql,
                @"\bLTRIM\s*\(\s*RTRIM\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)\s*\)",
                m => "TRIM(" + m.Groups["expr"].Value.Trim() + ")",
                RegexOptions.IgnoreCase);

            return Regex.Replace(
                sql,
                @"\bRTRIM\s*\(\s*LTRIM\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*\)\s*\)",
                m => "TRIM(" + m.Groups["expr"].Value.Trim() + ")",
                RegexOptions.IgnoreCase);
        }

        private static string RewriteSubstringFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bSUBSTRING\s*\(\s*(?<expr>[^()]+?)\s+FROM\s+(?<start>[^()]+?)\s+FOR\s+(?<length>[^()]+?)\s*\)",
                m => BuildSubstringExpression(targetProvider, m.Groups["expr"].Value.Trim(), m.Groups["start"].Value.Trim(), m.Groups["length"].Value.Trim()),
                RegexOptions.IgnoreCase);

            if (targetProvider == "mssql")
            {
                return Regex.Replace(
                    sql,
                    @"\bSUBSTR\s*\((?<args>[^()]*)\)",
                    m => RewriteFunctionName(m, "SUBSTRING"),
                    RegexOptions.IgnoreCase);
            }

            if (targetProvider == "oracle" || targetProvider == "sqlite")
            {
                return Regex.Replace(
                    sql,
                    @"\bSUBSTRING\s*\((?<args>[^()]*)\)",
                    m => RewriteFunctionName(m, "SUBSTR"),
                    RegexOptions.IgnoreCase);
            }

            return sql;
        }

        private static string BuildSubstringExpression(string targetProvider, string expr, string start, string length)
        {
            string functionName = (targetProvider == "oracle" || targetProvider == "sqlite") ? "SUBSTR" : "SUBSTRING";
            return functionName + "(" + expr + ", " + start + ", " + length + ")";
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

        private static string RewritePaddingFunctions(string selectSql, string targetProvider)
        {
            if (targetProvider != "mssql" && targetProvider != "sqlite") return selectSql;

            string sql = Regex.Replace(
                selectSql,
                @"\bLPAD\s*\((?<args>[^()]*)\)",
                m => RewritePadFunction(m, targetProvider, true),
                RegexOptions.IgnoreCase);

            return Regex.Replace(
                sql,
                @"\bRPAD\s*\((?<args>[^()]*)\)",
                m => RewritePadFunction(m, targetProvider, false),
                RegexOptions.IgnoreCase);
        }

        private static string RewritePadFunction(Match match, string targetProvider, bool leftPad)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count != 3) return match.Value;

            string length = args[1];
            string pad = args[2];
            if (targetProvider == "sqlite")
            {
                string value = "CAST(" + args[0] + " AS TEXT)";
                string repeatedPad = "REPLACE(HEX(ZEROBLOB(" + length + ")), '00', " + pad + ")";
                return leftPad
                    ? "SUBSTR(" + repeatedPad + " || " + value + ", -" + length + ", " + length + ")"
                    : "SUBSTR(" + value + " || " + repeatedPad + ", 1, " + length + ")";
            }

            string mssqlValue = "CAST(" + args[0] + " AS varchar(max))";
            if (leftPad)
            {
                return "RIGHT(REPLICATE(" + pad + ", " + length + ") + " + mssqlValue + ", " + length + ")";
            }

            return "LEFT(" + mssqlValue + " + REPLICATE(" + pad + ", " + length + "), " + length + ")";
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

        private static string RewritePatternMatchOperators(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bREGEXP_LIKE\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*(?<pattern>'(?:''|[^'])*')\s*\)",
                m => BuildRegexMatchExpression(targetProvider, m.Groups["expr"].Value.Trim(), m.Groups["pattern"].Value, m.Value),
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"(?<expr>\b[A-Za-z_][A-Za-z0-9_\.]*\b)\s+~\s+(?<pattern>'(?:''|[^'])*')",
                m => BuildRegexMatchExpression(targetProvider, m.Groups["expr"].Value.Trim(), m.Groups["pattern"].Value, m.Value),
                RegexOptions.IgnoreCase);

            if (targetProvider == "postgresql") return sql;

            return Regex.Replace(
                sql,
                @"(?<expr>\b[A-Za-z_][A-Za-z0-9_\.]*\b)\s+ILIKE\s+(?<pattern>'(?:''|[^'])*')",
                m => "LOWER(" + m.Groups["expr"].Value.Trim() + ") LIKE LOWER(" + m.Groups["pattern"].Value + ")",
                RegexOptions.IgnoreCase);
        }

        private static string BuildRegexMatchExpression(string targetProvider, string expr, string pattern, string original)
        {
            if (targetProvider == "postgresql") return expr + " ~ " + pattern;
            if (targetProvider == "mysql" || targetProvider == "oracle") return "REGEXP_LIKE(" + expr + ", " + pattern + ")";
            return original;
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

        private static string RewritePostgreSqlJsonOperators(string selectSql, string targetProvider)
        {
            if (targetProvider == "postgresql") return selectSql;

            string sql = Regex.Replace(
                selectSql,
                @"(?<expr>\b[A-Za-z_][A-Za-z0-9_\.]*\b|\([^)]+\))\s*(?<op>#>>|#>)\s*'(?<path>\{[^']+\})'",
                m =>
                {
                    string jsonPath = BuildJsonPathFromPostgreSqlArrayPath(m.Groups["path"].Value);
                    if (string.IsNullOrWhiteSpace(jsonPath)) return m.Value;
                    bool textValue = m.Groups["op"].Value == "#>>";
                    return BuildJsonExtractionForTarget(targetProvider, m.Groups["expr"].Value.Trim(), jsonPath, textValue, m.Value);
                },
                RegexOptions.IgnoreCase);

            sql = Regex.Replace(
                sql,
                @"(?<expr>\b[A-Za-z_][A-Za-z0-9_\.]*\b|\([^)]+\))\s*(?<op>->>|->)\s*(?:'(?<key>[^']+)'|(?<index>\d+))",
                m =>
                {
                    string pathPart = m.Groups["key"].Success
                        ? BuildJsonPathPart(m.Groups["key"].Value)
                        : "[" + m.Groups["index"].Value + "]";
                    if (string.IsNullOrWhiteSpace(pathPart)) return m.Value;
                    bool textValue = m.Groups["op"].Value == "->>";
                    return BuildJsonExtractionForTarget(targetProvider, m.Groups["expr"].Value.Trim(), "$" + pathPart, textValue, m.Value);
                },
                RegexOptions.IgnoreCase);

            return sql;
        }

        private static string BuildJsonExtractionForTarget(string targetProvider, string expr, string jsonPath, bool textValue, string original)
        {
            string escapedPath = EscapeSqlString(jsonPath);
            if (targetProvider == "mysql")
            {
                string extract = "JSON_EXTRACT(" + expr + ", '" + escapedPath + "')";
                return textValue ? "JSON_UNQUOTE(" + extract + ")" : extract;
            }
            if (targetProvider == "mssql" || targetProvider == "oracle")
            {
                return (textValue ? "JSON_VALUE" : "JSON_QUERY") + "(" + expr + ", '" + escapedPath + "')";
            }
            if (targetProvider == "sqlite")
            {
                return "json_extract(" + expr + ", '" + escapedPath + "')";
            }
            return original;
        }

        private static string BuildJsonPathFromPostgreSqlArrayPath(string pgPath)
        {
            if (string.IsNullOrWhiteSpace(pgPath) || pgPath.Length < 2 || pgPath[0] != '{' || pgPath[pgPath.Length - 1] != '}')
            {
                return "";
            }

            string body = pgPath.Substring(1, pgPath.Length - 2);
            if (string.IsNullOrWhiteSpace(body)) return "";
            string[] parts = body.Split(',');
            StringBuilder builder = new StringBuilder("$");
            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim().Trim('"');
                string pathPart = BuildJsonPathPart(part);
                if (string.IsNullOrWhiteSpace(pathPart)) return "";
                builder.Append(pathPart);
            }
            return builder.ToString();
        }

        private static string BuildJsonPathPart(string part)
        {
            if (string.IsNullOrWhiteSpace(part)) return "";
            part = part.Trim();
            if (Regex.IsMatch(part, @"^\d+$")) return "[" + part + "]";
            if (!Regex.IsMatch(part, @"^[A-Za-z_][A-Za-z0-9_]*$")) return "";
            return "." + part;
        }

        private static string RewriteJsonExistsFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bJSON_EXISTS\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<path>\$[^']*)'\s*\)",
                m => RewriteJsonExistsExpression(
                    targetProvider,
                    m.Groups["expr"].Value.Trim(),
                    m.Groups["path"].Value,
                    m.Value),
                RegexOptions.IgnoreCase);

            return Regex.Replace(
                sql,
                @"\bJSON_CONTAINS_PATH\s*\((?<args>[^()]*)\)",
                m => RewriteJsonContainsPathExpression(m, targetProvider),
                RegexOptions.IgnoreCase);
        }

        private static string RewriteJsonContainsPathExpression(Match match, string targetProvider)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count != 3) return match.Value;

            string mode = TrimSqlStringLiteral(args[1]).ToLowerInvariant();
            if (mode != "one") return match.Value;

            string path = TrimSqlStringLiteral(args[2]);
            return RewriteJsonExistsExpression(targetProvider, args[0], path, match.Value);
        }

        private static string RewriteJsonExistsExpression(string targetProvider, string expr, string jsonPath, string original)
        {
            if (string.IsNullOrWhiteSpace(jsonPath) || !jsonPath.StartsWith("$", StringComparison.Ordinal)) return original;

            string escapedPath = EscapeSqlString(jsonPath);
            if (targetProvider == "mysql")
            {
                return "JSON_CONTAINS_PATH(" + expr + ", 'one', '" + escapedPath + "')";
            }

            if (targetProvider == "postgresql")
            {
                string pgPath = BuildPostgreSqlJsonPath(jsonPath);
                if (jsonPath == "$") return expr + " IS NOT NULL";
                if (!string.IsNullOrWhiteSpace(pgPath)) return "(" + expr + "::jsonb #> " + pgPath + ") IS NOT NULL";
            }

            if (targetProvider == "sqlite")
            {
                return "json_type(" + expr + ", '" + escapedPath + "') IS NOT NULL";
            }

            if (targetProvider == "mssql")
            {
                return "(JSON_VALUE(" + expr + ", '" + escapedPath + "') IS NOT NULL OR JSON_QUERY(" + expr + ", '" + escapedPath + "') IS NOT NULL)";
            }

            if (targetProvider == "oracle")
            {
                return "JSON_EXISTS(" + expr + ", '" + escapedPath + "')";
            }

            return original;
        }

        private static string RewriteJsonLengthFunctions(string selectSql, string targetProvider)
        {
            string sql = Regex.Replace(
                selectSql,
                @"\bJSON_ARRAY_LENGTH\s*\((?<args>[^()]*)\)",
                m => RewriteJsonLengthFunction(m, targetProvider),
                RegexOptions.IgnoreCase);

            return Regex.Replace(
                sql,
                @"\bJSON_LENGTH\s*\((?<args>[^()]*)\)",
                m => RewriteJsonLengthFunction(m, targetProvider),
                RegexOptions.IgnoreCase);
        }

        private static string RewriteJsonLengthFunction(Match match, string targetProvider)
        {
            List<string> args = SplitFunctionArguments(match.Groups["args"].Value);
            if (args.Count < 1 || args.Count > 2) return match.Value;

            string expr = args[0];
            string path = args.Count == 2 ? TrimSqlStringLiteral(args[1]) : "$";
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("$", StringComparison.Ordinal)) return match.Value;

            string escapedPath = EscapeSqlString(path);
            if (targetProvider == "mysql")
            {
                return path == "$"
                    ? "JSON_LENGTH(" + expr + ")"
                    : "JSON_LENGTH(" + expr + ", '" + escapedPath + "')";
            }

            if (targetProvider == "postgresql")
            {
                string pgPath = BuildPostgreSqlJsonPath(path);
                if (path == "$") return "jsonb_array_length(" + expr + "::jsonb)";
                if (!string.IsNullOrWhiteSpace(pgPath)) return "jsonb_array_length(" + expr + "::jsonb #> " + pgPath + ")";
            }

            if (targetProvider == "sqlite")
            {
                return path == "$"
                    ? "json_array_length(" + expr + ")"
                    : "json_array_length(" + expr + ", '" + escapedPath + "')";
            }

            if (targetProvider == "mssql")
            {
                return path == "$"
                    ? "(SELECT COUNT(*) FROM OPENJSON(" + expr + "))"
                    : "(SELECT COUNT(*) FROM OPENJSON(" + expr + ", '" + escapedPath + "'))";
            }

            if (targetProvider == "oracle")
            {
                string lengthPath = BuildOracleJsonLengthPath(path);
                if (!string.IsNullOrWhiteSpace(lengthPath))
                {
                    return "JSON_VALUE(" + expr + ", '" + EscapeSqlString(lengthPath) + "' RETURNING NUMBER)";
                }
            }

            return match.Value;
        }

        private static string RewriteJsonTableExpressions(string selectSql, string targetProvider)
        {
            if (targetProvider == "mysql" || targetProvider == "oracle") return selectSql;
            if (targetProvider != "postgresql" && targetProvider != "mssql" && targetProvider != "sqlite") return selectSql;

            string sql = selectSql ?? string.Empty;
            Dictionary<string, List<JsonTableColumn>> sqliteColumnMappings = targetProvider == "sqlite"
                ? new Dictionary<string, List<JsonTableColumn>>(StringComparer.OrdinalIgnoreCase)
                : null;
            int searchIndex = 0;
            while (searchIndex < sql.Length)
            {
                int jsonTableIndex = IndexOfKeyword(sql, "JSON_TABLE", searchIndex);
                if (jsonTableIndex < 0) break;

                int openParen = SkipWhitespaceRight(sql, jsonTableIndex + "JSON_TABLE".Length);
                if (openParen >= sql.Length || sql[openParen] != '(')
                {
                    searchIndex = jsonTableIndex + "JSON_TABLE".Length;
                    continue;
                }

                int closeParen = FindMatchingCloseParen(sql, openParen);
                if (closeParen < 0) break;

                string inner = sql.Substring(openParen + 1, closeParen - openParen - 1);
                string sourceExpr;
                string arrayPath;
                List<JsonTableColumn> columns;
                if (!TryParseJsonTableArguments(inner, out sourceExpr, out arrayPath, out columns))
                {
                    searchIndex = closeParen + 1;
                    continue;
                }

                int replacementEnd = closeParen + 1;
                string alias = ReadJsonTableAlias(sql, closeParen + 1, out replacementEnd);
                if (string.IsNullOrWhiteSpace(alias)) alias = "json_rows";

                string replacement = targetProvider == "postgresql"
                    ? BuildPostgreSqlJsonTableExpression(sourceExpr, arrayPath, columns, alias)
                    : (targetProvider == "mssql"
                        ? BuildSqlServerJsonTableExpression(sourceExpr, arrayPath, columns, alias)
                        : BuildSqliteJsonTableExpression(sourceExpr, arrayPath, alias));

                if (string.IsNullOrWhiteSpace(replacement))
                {
                    searchIndex = closeParen + 1;
                    continue;
                }

                if (targetProvider == "sqlite")
                {
                    sqliteColumnMappings[alias] = columns;
                }

                sql = sql.Substring(0, jsonTableIndex) + replacement + sql.Substring(replacementEnd);
                searchIndex = jsonTableIndex + replacement.Length;
            }

            if (sqliteColumnMappings != null && sqliteColumnMappings.Count > 0)
            {
                sql = RewriteSqliteJsonTableColumnReferences(sql, sqliteColumnMappings);
            }

            return sql;
        }

        private static bool TryParseJsonTableArguments(string inner, out string sourceExpr, out string arrayPath, out List<JsonTableColumn> columns)
        {
            sourceExpr = "";
            arrayPath = "";
            columns = new List<JsonTableColumn>();

            List<string> args = SplitTopLevelSqlList(inner);
            if (args.Count != 2) return false;

            Match match = Regex.Match(
                args[1],
                @"^\s*'(?<path>(?:''|[^'])*)'\s+COLUMNS\s*\((?<columns>.*)\)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return false;

            sourceExpr = args[0].Trim();
            arrayPath = TrimSqlStringLiteral("'" + match.Groups["path"].Value + "'");
            List<string> columnTexts = SplitTopLevelSqlList(match.Groups["columns"].Value);
            if (columnTexts.Count == 0) return false;

            foreach (string columnText in columnTexts)
            {
                JsonTableColumn column;
                if (!TryParseJsonTableColumn(columnText, out column)) return false;
                columns.Add(column);
            }

            return !string.IsNullOrWhiteSpace(sourceExpr) &&
                   !string.IsNullOrWhiteSpace(arrayPath) &&
                   columns.Count > 0;
        }

        private static bool TryParseJsonTableColumn(string columnText, out JsonTableColumn column)
        {
            column = null;
            Match match = Regex.Match(
                columnText ?? "",
                @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*|""[^""]+""|`[^`]+`|\[[^\]]+\])\s+(?<type>[A-Za-z][A-Za-z0-9_]*(?:\s*\([^)]*\))?)\s+PATH\s+'(?<path>(?:''|[^'])*)'\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return false;

            string name = NormalizeJsonTableIdentifier(match.Groups["name"].Value);
            string path = TrimSqlStringLiteral("'" + match.Groups["path"].Value + "'");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || !path.StartsWith("$", StringComparison.Ordinal)) return false;

            column = new JsonTableColumn
            {
                Name = name,
                SqlType = match.Groups["type"].Value.Trim(),
                JsonPath = path
            };
            return true;
        }

        private static string BuildPostgreSqlJsonTableExpression(string sourceExpr, string arrayPath, List<JsonTableColumn> columns, string alias)
        {
            string rowSource = BuildPostgreSqlJsonTableRowSource(sourceExpr, arrayPath);
            if (string.IsNullOrWhiteSpace(rowSource)) return "";

            List<string> projections = new List<string>();
            foreach (JsonTableColumn column in columns)
            {
                string expression = BuildPostgreSqlJsonTableColumnExpression("json_item.value", column);
                if (string.IsNullOrWhiteSpace(expression)) return "";
                projections.Add(expression + " AS " + column.Name);
            }

            return "LATERAL (SELECT " + string.Join(", ", projections.ToArray()) +
                   " FROM jsonb_array_elements(" + rowSource + ") AS json_item(value)) AS " + alias;
        }

        private static string BuildPostgreSqlJsonTableRowSource(string sourceExpr, string arrayPath)
        {
            string path = NormalizeJsonTableArrayPath(arrayPath);
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (path == "$") return sourceExpr + "::jsonb";

            string pgPath = BuildPostgreSqlJsonPath(path);
            if (string.IsNullOrWhiteSpace(pgPath)) return "";
            return sourceExpr + "::jsonb #> " + pgPath;
        }

        private static string BuildPostgreSqlJsonTableColumnExpression(string rowExpr, JsonTableColumn column)
        {
            string textPath = BuildPostgreSqlJsonPath(column.JsonPath);
            if (string.IsNullOrWhiteSpace(textPath)) return "";

            string type = MapJsonTableTypeForPostgreSql(column.SqlType);
            string textExpression = rowExpr + " #>> " + textPath;
            if (type == "text") return textExpression;
            if (type == "boolean") return "CAST(" + textExpression + " AS boolean)";
            if (type == "date") return "CAST(" + textExpression + " AS date)";
            if (type == "timestamp") return "CAST(" + textExpression + " AS timestamp)";
            if (type == "double precision") return "CAST(" + textExpression + " AS double precision)";
            return "CAST(" + textExpression + " AS " + type + ")";
        }

        private static string BuildSqlServerJsonTableExpression(string sourceExpr, string arrayPath, List<JsonTableColumn> columns, string alias)
        {
            string path = NormalizeJsonTableArrayPath(arrayPath);
            if (string.IsNullOrWhiteSpace(path)) return "";

            List<string> definitions = new List<string>();
            foreach (JsonTableColumn column in columns)
            {
                definitions.Add(column.Name + " " + MapJsonTableTypeForSqlServer(column.SqlType) + " '" + EscapeSqlString(column.JsonPath) + "'");
            }

            string openJson = path == "$"
                ? "OPENJSON(" + sourceExpr + ")"
                : "OPENJSON(" + sourceExpr + ", '" + EscapeSqlString(path) + "')";
            return openJson + " WITH (" + string.Join(", ", definitions.ToArray()) + ") AS " + alias;
        }

        private static string BuildSqliteJsonTableExpression(string sourceExpr, string arrayPath, string alias)
        {
            string path = NormalizeJsonTableArrayPath(arrayPath);
            if (string.IsNullOrWhiteSpace(path)) return "";

            string jsonEach = path == "$"
                ? "json_each(" + sourceExpr + ")"
                : "json_each(" + sourceExpr + ", '" + EscapeSqlString(path) + "')";
            return jsonEach + " AS " + alias;
        }

        private static string RewriteSqliteJsonTableColumnReferences(string sql, Dictionary<string, List<JsonTableColumn>> mappings)
        {
            string output = sql ?? string.Empty;
            foreach (KeyValuePair<string, List<JsonTableColumn>> mapping in mappings)
            {
                string alias = mapping.Key;
                foreach (JsonTableColumn column in mapping.Value)
                {
                    string expression = BuildSqliteJsonTableColumnExpression(alias, column);
                    if (string.IsNullOrWhiteSpace(expression)) continue;
                    output = ReplaceQualifiedIdentifierOutsideStrings(output, alias, column.Name, expression);
                }
            }

            return output;
        }

        private static string BuildSqliteJsonTableColumnExpression(string alias, JsonTableColumn column)
        {
            string expression = "json_extract(" + alias + ".value, '" + EscapeSqlString(column.JsonPath) + "')";
            string type = MapJsonTableTypeForSqlite(column.SqlType);
            if (string.IsNullOrWhiteSpace(type)) return expression;
            return "CAST(" + expression + " AS " + type + ")";
        }

        private static string MapJsonTableTypeForSqlite(string sqlType)
        {
            string type = (sqlType ?? "").Trim().ToLowerInvariant();
            if (Regex.IsMatch(type, @"^(int|integer|bigint|bool|boolean|bit)$")) return "INTEGER";
            if (Regex.IsMatch(type, @"^(decimal|numeric|double|float|real)(\s*\([^)]*\))?$")) return "REAL";
            return "";
        }

        private static string ReplaceQualifiedIdentifierOutsideStrings(string sql, string alias, string column, string replacement)
        {
            if (string.IsNullOrEmpty(sql) || string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(column)) return sql;

            StringBuilder output = new StringBuilder(sql.Length + Math.Max(0, replacement.Length - alias.Length - column.Length - 1));
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            bool inBracketQuote = false;
            bool inBacktickQuote = false;

            for (int i = 0; i < sql.Length;)
            {
                char ch = sql[i];
                if (inSingleQuote)
                {
                    output.Append(ch);
                    if (ch == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        output.Append(sql[++i]);
                    }
                    else if (ch == '\'')
                    {
                        inSingleQuote = false;
                    }
                    i++;
                    continue;
                }

                if (inDoubleQuote)
                {
                    output.Append(ch);
                    if (ch == '"') inDoubleQuote = false;
                    i++;
                    continue;
                }

                if (inBracketQuote)
                {
                    output.Append(ch);
                    if (ch == ']') inBracketQuote = false;
                    i++;
                    continue;
                }

                if (inBacktickQuote)
                {
                    output.Append(ch);
                    if (ch == '`') inBacktickQuote = false;
                    i++;
                    continue;
                }

                if (ch == '\'') { inSingleQuote = true; output.Append(ch); i++; continue; }
                if (ch == '"') { inDoubleQuote = true; output.Append(ch); i++; continue; }
                if (ch == '[') { inBracketQuote = true; output.Append(ch); i++; continue; }
                if (ch == '`') { inBacktickQuote = true; output.Append(ch); i++; continue; }

                if (MatchesQualifiedIdentifierAt(sql, i, alias, column))
                {
                    output.Append(replacement);
                    i += alias.Length + 1 + column.Length;
                    continue;
                }

                output.Append(ch);
                i++;
            }

            return output.ToString();
        }

        private static bool MatchesQualifiedIdentifierAt(string sql, int index, string alias, string column)
        {
            int length = alias.Length + 1 + column.Length;
            if (index < 0 || index + length > sql.Length) return false;
            if (!string.Equals(sql.Substring(index, alias.Length), alias, StringComparison.OrdinalIgnoreCase)) return false;
            if (sql[index + alias.Length] != '.') return false;
            if (!string.Equals(sql.Substring(index + alias.Length + 1, column.Length), column, StringComparison.OrdinalIgnoreCase)) return false;

            int before = index - 1;
            int after = index + length;
            return (before < 0 || !IsSimpleIdentifierChar(sql[before])) &&
                   (after >= sql.Length || !IsSimpleIdentifierChar(sql[after]));
        }

        private static string NormalizeJsonTableArrayPath(string arrayPath)
        {
            string path = (arrayPath ?? "").Trim();
            if (path == "$[*]") return "$";
            if (path.EndsWith("[*]", StringComparison.Ordinal)) return path.Substring(0, path.Length - 3);
            return path;
        }

        private static string MapJsonTableTypeForPostgreSql(string sqlType)
        {
            string type = (sqlType ?? "").Trim().ToLowerInvariant();
            if (Regex.IsMatch(type, @"^(int|integer)$")) return "integer";
            if (Regex.IsMatch(type, @"^bigint$")) return "bigint";
            if (Regex.IsMatch(type, @"^(decimal|numeric)(\s*\([^)]*\))?$")) return Regex.Replace(type, @"^decimal", "numeric");
            if (Regex.IsMatch(type, @"^(double|float|real)(\s*\([^)]*\))?$")) return "double precision";
            if (Regex.IsMatch(type, @"^(bool|boolean|bit)$")) return "boolean";
            if (Regex.IsMatch(type, @"^date$")) return "date";
            if (Regex.IsMatch(type, @"^(datetime|datetime2|timestamp)$")) return "timestamp";
            return "text";
        }

        private static string MapJsonTableTypeForSqlServer(string sqlType)
        {
            string type = (sqlType ?? "").Trim().ToLowerInvariant();
            if (Regex.IsMatch(type, @"^(int|integer)$")) return "int";
            if (Regex.IsMatch(type, @"^bigint$")) return "bigint";
            if (Regex.IsMatch(type, @"^(decimal|numeric)(\s*\([^)]*\))?$")) return type;
            if (Regex.IsMatch(type, @"^(double|float|real)(\s*\([^)]*\))?$")) return "float";
            if (Regex.IsMatch(type, @"^(bool|boolean|bit)$")) return "bit";
            if (Regex.IsMatch(type, @"^date$")) return "date";
            if (Regex.IsMatch(type, @"^(datetime|datetime2|timestamp)$")) return "datetime2";

            Match length = Regex.Match(type, @"^(?:varchar|nvarchar|char|nchar)\s*\((?<length>max|\d+)\)$", RegexOptions.IgnoreCase);
            if (length.Success) return "nvarchar(" + length.Groups["length"].Value + ")";
            return "nvarchar(max)";
        }

        private static string NormalizeJsonTableIdentifier(string identifier)
        {
            string text = (identifier ?? "").Trim();
            if (text.Length >= 2 && ((text[0] == '"' && text[text.Length - 1] == '"') ||
                                     (text[0] == '`' && text[text.Length - 1] == '`') ||
                                     (text[0] == '[' && text[text.Length - 1] == ']')))
            {
                text = text.Substring(1, text.Length - 2);
            }

            return Regex.IsMatch(text, @"^[A-Za-z_][A-Za-z0-9_]*$") ? text : "";
        }

        private static string ReadJsonTableAlias(string sql, int startIndex, out int aliasEnd)
        {
            int index = SkipWhitespaceRight(sql, startIndex);
            aliasEnd = startIndex;
            if (index >= sql.Length) return "";

            if (StartsWithKeywordAt(sql, index, "AS"))
            {
                index = SkipWhitespaceRight(sql, index + 2);
            }

            if (index >= sql.Length || !IsIdentifierStartChar(sql[index])) return "";
            int end = index + 1;
            while (end < sql.Length && IsSimpleIdentifierChar(sql[end])) end++;

            aliasEnd = end;
            return sql.Substring(index, end - index);
        }

        private static int IndexOfKeyword(string text, string keyword, int startIndex)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword)) return -1;

            int index = Math.Max(0, startIndex);
            while (index < text.Length)
            {
                int found = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0) return -1;
                int before = found - 1;
                int after = found + keyword.Length;
                bool hasLeftBoundary = before < 0 || !IsSimpleIdentifierChar(text[before]);
                bool hasRightBoundary = after >= text.Length || !IsSimpleIdentifierChar(text[after]);
                if (hasLeftBoundary && hasRightBoundary) return found;
                index = found + keyword.Length;
            }

            return -1;
        }

        private static bool StartsWithKeywordAt(string text, int index, string keyword)
        {
            if (index < 0 || string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword)) return false;
            if (index + keyword.Length > text.Length) return false;
            if (!string.Equals(text.Substring(index, keyword.Length), keyword, StringComparison.OrdinalIgnoreCase)) return false;

            int before = index - 1;
            int after = index + keyword.Length;
            return (before < 0 || !IsSimpleIdentifierChar(text[before])) &&
                   (after >= text.Length || !IsSimpleIdentifierChar(text[after]));
        }

        private static bool IsIdentifierStartChar(char ch)
        {
            return char.IsLetter(ch) || ch == '_';
        }

        private static bool IsSimpleIdentifierChar(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch == '_';
        }

        private static List<string> SplitTopLevelSqlList(string text)
        {
            List<string> items = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            bool inBracketQuote = false;
            bool inBacktickQuote = false;
            int depth = 0;

            for (int i = 0; i < (text ?? string.Empty).Length; i++)
            {
                char ch = text[i];
                if (inSingleQuote)
                {
                    current.Append(ch);
                    if (ch == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        current.Append(text[++i]);
                    }
                    else if (ch == '\'')
                    {
                        inSingleQuote = false;
                    }
                    continue;
                }

                if (inDoubleQuote)
                {
                    current.Append(ch);
                    if (ch == '"') inDoubleQuote = false;
                    continue;
                }

                if (inBracketQuote)
                {
                    current.Append(ch);
                    if (ch == ']') inBracketQuote = false;
                    continue;
                }

                if (inBacktickQuote)
                {
                    current.Append(ch);
                    if (ch == '`') inBacktickQuote = false;
                    continue;
                }

                if (ch == '\'') { inSingleQuote = true; current.Append(ch); continue; }
                if (ch == '"') { inDoubleQuote = true; current.Append(ch); continue; }
                if (ch == '[') { inBracketQuote = true; current.Append(ch); continue; }
                if (ch == '`') { inBacktickQuote = true; current.Append(ch); continue; }
                if (ch == '(') { depth++; current.Append(ch); continue; }
                if (ch == ')') { if (depth > 0) depth--; current.Append(ch); continue; }

                if (ch == ',' && depth == 0)
                {
                    string item = current.ToString().Trim();
                    if (item.Length > 0) items.Add(item);
                    current.Length = 0;
                    continue;
                }

                current.Append(ch);
            }

            string tail = current.ToString().Trim();
            if (tail.Length > 0) items.Add(tail);
            return items;
        }

        private static string TrimSqlStringLiteral(string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length >= 2 && text[0] == '\'' && text[text.Length - 1] == '\'')
            {
                return text.Substring(1, text.Length - 2).Replace("''", "'");
            }

            return text;
        }

        private static string BuildOracleJsonLengthPath(string jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath) || !jsonPath.StartsWith("$", StringComparison.Ordinal)) return "";
            return jsonPath + ".size()";
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

        private static string RewriteJsonQueryFunctions(string selectSql, string targetProvider)
        {
            return Regex.Replace(
                selectSql,
                @"\bJSON_QUERY\s*\(\s*(?<expr>[^,()]+(?:\([^)]*\))?)\s*,\s*'(?<path>\$[^']*)'\s*\)",
                m =>
                {
                    string expr = m.Groups["expr"].Value.Trim();
                    string path = m.Groups["path"].Value;
                    string escapedPath = EscapeSqlString(path);
                    if (targetProvider == "mysql") return "JSON_EXTRACT(" + expr + ", '" + escapedPath + "')";
                    if (targetProvider == "postgresql")
                    {
                        string pgPath = BuildPostgreSqlJsonPath(path);
                        if (!string.IsNullOrWhiteSpace(pgPath)) return expr + " #> " + pgPath;
                    }
                    if (targetProvider == "sqlite") return "json_extract(" + expr + ", '" + escapedPath + "')";
                    return m.Value;
                },
                RegexOptions.IgnoreCase);
        }

        private static string BuildPostgreSqlJsonTextPath(string jsonPath)
        {
            return BuildPostgreSqlJsonPath(jsonPath);
        }

        private static string BuildPostgreSqlJsonPath(string jsonPath)
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

        private static string BuildDateParseExpression(string targetProvider, string expr, string translatedPattern, bool forceDateTime)
        {
            string escapedPattern = EscapeSqlString(translatedPattern);
            if (targetProvider == "mysql") return "STR_TO_DATE(" + expr + ", '" + escapedPattern + "')";
            if (targetProvider == "sqlite") return forceDateTime || IsDateTimePattern(translatedPattern) ? "datetime(" + expr + ")" : "date(" + expr + ")";
            if (targetProvider == "mssql")
            {
                string style = GetSqlServerParseStyle(translatedPattern);
                return string.IsNullOrWhiteSpace(style)
                    ? "CONVERT(datetime, " + expr + ")"
                    : "CONVERT(" + (forceDateTime || style != "23" ? "datetime" : "date") + ", " + expr + ", " + style + ")";
            }
            if (targetProvider == "postgresql" && (forceDateTime || IsDateTimePattern(translatedPattern)))
            {
                return "TO_TIMESTAMP(" + expr + ", '" + escapedPattern + "')";
            }
            return "TO_DATE(" + expr + ", '" + escapedPattern + "')";
        }

        private static string GetSqlServerParseStyle(string translatedPattern)
        {
            string pattern = (translatedPattern ?? "").Trim();
            if (string.Equals(pattern, "yyyy-MM-dd", StringComparison.OrdinalIgnoreCase)) return "23";
            if (string.Equals(pattern, "yyyy-MM-dd HH:mm:ss", StringComparison.OrdinalIgnoreCase)) return "120";
            return "";
        }

        private static bool IsDateTimePattern(string translatedPattern)
        {
            return Regex.IsMatch(translatedPattern ?? "", @"(HH|hh|%H|%h|HH24|HH12)", RegexOptions.IgnoreCase);
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

            if ((provider.Equals("mssql", StringComparison.OrdinalIgnoreCase) ||
                 provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase)) &&
                Regex.IsMatch(featureSql, @"\bREGEXP_LIKE\s*\(|\b[A-Za-z_][A-Za-z0-9_\.]*\s+~\s+", RegexOptions.IgnoreCase))
            {
                reason = "目標資料庫沒有通用內建正規表示式比對語法，無法安全自動轉換";
                return true;
            }

            if (!provider.Equals("mysql", StringComparison.OrdinalIgnoreCase) &&
                !provider.Equals("oracle", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(featureSql, @"\bJSON_TABLE\s*\(", RegexOptions.IgnoreCase))
            {
                reason = "JSON_TABLE 語法無法安全自動轉換為目標資料庫";
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

            return ReplaceOutsideSingleQuotedStrings(selectSql, segment =>
            {
                string sql = Regex.Replace(segment, @"`([^`]+)`", m => QuoteIdentifier(m.Groups[1].Value, openQuote, closeQuote));
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
            });
        }

        private static string ReplaceOutsideSingleQuotedStrings(string sql, Func<string, string> replaceSegment)
        {
            if (string.IsNullOrEmpty(sql)) return "";
            if (replaceSegment == null) return sql;

            StringBuilder output = new StringBuilder(sql.Length);
            StringBuilder segment = new StringBuilder();
            for (int i = 0; i < sql.Length; i++)
            {
                char ch = sql[i];
                if (ch != '\'')
                {
                    segment.Append(ch);
                    continue;
                }

                if (segment.Length > 0)
                {
                    output.Append(replaceSegment(segment.ToString()));
                    segment.Length = 0;
                }

                output.Append(ch);
                i++;
                while (i < sql.Length)
                {
                    output.Append(sql[i]);
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            output.Append(sql[i + 1]);
                            i += 2;
                            continue;
                        }
                        break;
                    }
                    i++;
                }
            }

            if (segment.Length > 0) output.Append(replaceSegment(segment.ToString()));
            return output.ToString();
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
