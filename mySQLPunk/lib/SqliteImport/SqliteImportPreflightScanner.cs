using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace mySQLPunk.lib.SqliteImport
{
    public static class SqliteImportPreflightScanner
    {
        private const int MaxBatchBytes = 32 * 1024 * 1024; // 32 MiB
        private static readonly Regex ManifestTableRegex = new Regex(@"^--\s*Manifest-Table:\s*([^\s|]+)\s*\|\s*Rows:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ManifestTablesRegex = new Regex(@"^--\s*Tables:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ManifestRowsRegex = new Regex(@"^--\s*TotalRows:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ManifestSha256Regex = new Regex(@"^--\s*Manifest-SHA256:\s*([a-fA-F0-9]{64})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] SpatialKeywords = new string[]
        {
            "InitSpatialMetaData", "AddGeometryColumn", "CreateSpatialIndex",
            "mod_spatialite", "spatial_ref_sys", "geometry_columns",
            "gpkg_spatial_ref_sys", "gpkg_geometry_columns", "gpkg_contents",
            "GeomFromText", "ST_GeomFromText", "AsText", "ST_AsText",
            "PointFromText", "LineFromText", "PolyFromText"
        };

        public static SqliteImportPreflight Scan(string filePath, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path is required", nameof(filePath));
            if (!File.Exists(filePath))
                return new SqliteImportPreflight(false, 0, 0, false, 0, 0, null, false, new[] { "File not found: " + filePath });

            long fileBytes = new FileInfo(filePath).Length;
            var issues = new List<string>();
            int statementCount = 0;
            bool hasManifest = false;
            int manifestTables = 0;
            long manifestRows = 0;
            string manifestSha = null;
            bool detectedSpatiaLite = false;

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                using (var reader = new StreamReader(fs, Encoding.UTF8, true, 65536))
                {
                    bool inSingleQuote = false;
                    bool inDoubleQuote = false;
                    bool inBracket = false;
                    bool inBlockComment = false;
                    int currentBatchLength = 0;
                    bool hasStatementContent = false;
                    int lineIndex = 0;
                    string line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        lineIndex++;

                        // 檢查 embedded NUL (非法字元或編碼毀損)
                        if (line.IndexOf('\0') >= 0)
                        {
                            issues.Add($"Line {lineIndex}: Contains Embedded NUL character (possible malformed encoding or corrupted binary file).");
                            break;
                        }

                        string trimmed = line.Trim();

                        // 檢查 Manifest
                        if (trimmed.StartsWith("--"))
                        {
                            if (trimmed.Contains("mySQLPunk Native Manifest"))
                            {
                                hasManifest = true;
                            }
                            else if (hasManifest)
                            {
                                var mTables = ManifestTablesRegex.Match(trimmed);
                                if (mTables.Success && int.TryParse(mTables.Groups[1].Value, out int mt))
                                {
                                    manifestTables = mt;
                                    continue;
                                }
                                var mRows = ManifestRowsRegex.Match(trimmed);
                                if (mRows.Success && long.TryParse(mRows.Groups[1].Value, out long mr))
                                {
                                    manifestRows = mr;
                                    continue;
                                }
                                var mTable = ManifestTableRegex.Match(trimmed);
                                if (mTable.Success)
                                {
                                    if (manifestTables == 0) manifestTables++;
                                    if (long.TryParse(mTable.Groups[2].Value, out long r) && manifestRows == 0) manifestRows += r;
                                    continue;
                                }
                                var mSha = ManifestSha256Regex.Match(trimmed);
                                if (mSha.Success)
                                {
                                    manifestSha = mSha.Groups[1].Value;
                                }
                            }

                            // 註解行若在引號外，不計入語句實質內容
                            if (!inSingleQuote && !inDoubleQuote && !inBlockComment)
                            {
                                continue;
                            }
                        }

                        // 探測 SpatiaLite / GeoPackage 關鍵字
                        if (!detectedSpatiaLite)
                        {
                            for (int k = 0; k < SpatialKeywords.Length; k++)
                            {
                                if (line.IndexOf(SpatialKeywords[k], StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    detectedSpatiaLite = true;
                                    break;
                                }
                            }
                        }

                        // 逐字元狀態機解析，以精準識別分號
                        int len = line.Length;
                        for (int i = 0; i < len; i++)
                        {
                            char c = line[i];

                            if (inBlockComment)
                            {
                                if (c == '*' && i + 1 < len && line[i + 1] == '/')
                                {
                                    inBlockComment = false;
                                    i++;
                                }
                                continue;
                            }

                            if (!inSingleQuote && !inDoubleQuote && !inBracket)
                            {
                                if (c == '-' && i + 1 < len && line[i + 1] == '-')
                                {
                                    // 遇到單行註解，本行後續忽略
                                    break;
                                }
                                if (c == '/' && i + 1 < len && line[i + 1] == '*')
                                {
                                    inBlockComment = true;
                                    i++;
                                    continue;
                                }
                            }

                            if (c == '\'' && !inDoubleQuote && !inBracket)
                            {
                                inSingleQuote = !inSingleQuote;
                                hasStatementContent = true;
                                currentBatchLength++;
                                continue;
                            }

                            if (c == '"' && !inSingleQuote && !inBracket)
                            {
                                inDoubleQuote = !inDoubleQuote;
                                hasStatementContent = true;
                                currentBatchLength++;
                                continue;
                            }

                            if (c == '[' && !inSingleQuote && !inDoubleQuote)
                            {
                                inBracket = true;
                                hasStatementContent = true;
                                currentBatchLength++;
                                continue;
                            }

                            if (c == ']' && inBracket)
                            {
                                inBracket = false;
                                hasStatementContent = true;
                                currentBatchLength++;
                                continue;
                            }

                            if (!char.IsWhiteSpace(c))
                            {
                                hasStatementContent = true;
                            }
                            currentBatchLength++;

                            // 檢查語句終止分號（必須在字串引號與註解之外）
                            if (c == ';' && !inSingleQuote && !inDoubleQuote && !inBracket)
                            {
                                if (hasStatementContent)
                                {
                                    statementCount++;
                                    hasStatementContent = false;
                                    currentBatchLength = 0;
                                }
                            }
                        }

                        // 單批次大小檢查 (32 MiB)
                        if (currentBatchLength > MaxBatchBytes)
                        {
                            issues.Add($"Line {lineIndex}: Single SQL statement exceeds safe limit of 32 MiB.");
                            break;
                        }
                    }

                    if (hasStatementContent)
                    {
                        statementCount++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return new SqliteImportPreflight(false, fileBytes, statementCount, hasManifest, manifestTables, manifestRows, manifestSha, detectedSpatiaLite, new[] { "Scan cancelled by user." });
            }
            catch (Exception ex)
            {
                issues.Add("Error during preflight scan: " + ex.Message);
            }

            bool passed = issues.Count == 0;
            return new SqliteImportPreflight(
                passed,
                fileBytes,
                statementCount,
                hasManifest,
                manifestTables,
                manifestRows,
                manifestSha,
                detectedSpatiaLite,
                issues);
        }
    }
}
