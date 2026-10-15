using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace mySQLPunk.lib.OracleImport
{
    public sealed class OracleImportPreflightScanner
    {
        private const int MaxBatchSizeBytes = 32 * 1024 * 1024; // 32 MiB

        private static readonly string[] SpatialKeywords = new[]
        {
            "sdo_geometry", "mdsys.sdo_geometry", "sdo_util", "user_sdo_geom_metadata",
            "spatial_index", "sdo_dim_array", "sdo_dim_element", "sdo_point_type"
        };

        private static readonly Regex PlSqlStartRegex = new Regex(
            @"^\s*(DECLARE\b|BEGIN\b|CREATE\s+(OR\s+REPLACE\s+)?(PROCEDURE|FUNCTION|PACKAGE|TRIGGER|TYPE)\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public OracleImportPreflight Scan(string filePath, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return new OracleImportPreflight(false, "未指定匯入檔案路徑。");

            if (!File.Exists(filePath))
                return new OracleImportPreflight(false, string.Format("找不到指定的 SQL 檔案: {0}", filePath));

            var fi = new FileInfo(filePath);
            var result = new OracleImportPreflight
            {
                FileSizeBytes = fi.Length,
                CanProceed = true
            };

            Encoding detectedEncoding = DetectEncoding(filePath);
            result.DetectedEncoding = detectedEncoding;

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                using (var reader = new StreamReader(fs, detectedEncoding, true, 65536))
                {
                    int statementCount = 0;
                    long currentStatementBytes = 0;
                    bool hasStatementContent = false;
                    bool inSingleQuote = false;
                    bool inDoubleQuote = false;
                    bool inLineComment = false;
                    int blockCommentDepth = 0;
                    bool inPlSqlBlock = false;

                    var spatialFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    string line;
                    int lineNumber = 0;

                    while ((line = reader.ReadLine()) != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        lineNumber++;

                        // NUL byte 損毀快速檢查
                        if (line.IndexOf('\0') >= 0)
                        {
                            result.CanProceed = false;
                            result.Issues.Add(string.Format("第 {0} 行包含二進位 NUL 空字元，檔案可能已損毀或非純文字 SQL 腳本。", lineNumber));
                            return result;
                        }

                        // 檢查 Native Manifest 標記
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("--", StringComparison.Ordinal))
                        {
                            if (trimmed.Contains("mySQLPunk Native Manifest"))
                            {
                                result.ContainsManifest = true;
                            }

                            var matchTable = Regex.Match(trimmed, @"^--\s*Manifest-Table:\s*([^\s|]+)\s*\|\s*Rows:\s*(\d+)", RegexOptions.IgnoreCase);
                            if (matchTable.Success && long.TryParse(matchTable.Groups[2].Value, out long rows))
                            {
                                result.ManifestDeclaredTables[matchTable.Groups[1].Value] = rows;
                            }

                            var matchSha = Regex.Match(trimmed, @"^--\s*Manifest-Checksum-SHA256:\s*([a-fA-F0-9]{64})", RegexOptions.IgnoreCase);
                            if (matchSha.Success)
                            {
                                result.ManifestSha256 = matchSha.Groups[1].Value.ToLowerInvariant();
                            }
                        }

                        // 探測 Oracle Spatial 空間關鍵字
                        string lowerLine = line.ToLowerInvariant();
                        foreach (var kw in SpatialKeywords)
                        {
                            if (lowerLine.Contains(kw))
                            {
                                result.HasSpatialFeatures = true;
                                if (spatialFound.Add(kw))
                                {
                                    result.DetectedSpatialKeywords.Add(kw);
                                }
                            }
                        }

                        // 檢查是否開展 PL/SQL 區塊 (DECLARE / BEGIN / CREATE PROCEDURE 等)
                        if (!inPlSqlBlock && !inSingleQuote && !inDoubleQuote && blockCommentDepth == 0)
                        {
                            if (PlSqlStartRegex.IsMatch(line))
                            {
                                inPlSqlBlock = true;
                                result.HasPlSqlBlocks = true;
                                result.PlSqlBlocksCount++;
                            }
                        }

                        // 檢查 PL/SQL 區塊結束符：獨立行斜線 /
                        if (inPlSqlBlock && !inSingleQuote && !inDoubleQuote && blockCommentDepth == 0)
                        {
                            if (trimmed == "/")
                            {
                                inPlSqlBlock = false;
                                if (hasStatementContent)
                                {
                                    statementCount++;
                                    hasStatementContent = false;
                                    currentStatementBytes = 0;
                                }
                                continue;
                            }
                        }

                        int len = line.Length;
                        for (int i = 0; i < len; i++)
                        {
                            char c = line[i];

                            // 1. 單行註解
                            if (inLineComment)
                            {
                                continue;
                            }

                            // 2. 塊註解 /* ... */
                            if (blockCommentDepth > 0)
                            {
                                if (c == '*' && i + 1 < len && line[i + 1] == '/')
                                {
                                    blockCommentDepth--;
                                    i++;
                                }
                                continue;
                            }

                            // 3. 單引號字串 '...'
                            if (inSingleQuote)
                            {
                                hasStatementContent = true;
                                currentStatementBytes += (c > 127) ? 2 : 1;
                                if (c == '\'')
                                {
                                    if (i + 1 < len && line[i + 1] == '\'')
                                    {
                                        i++; // 跳過 ''
                                        currentStatementBytes++;
                                    }
                                    else
                                    {
                                        inSingleQuote = false;
                                    }
                                }
                                continue;
                            }

                            // 4. 雙引號識別字 "..."
                            if (inDoubleQuote)
                            {
                                hasStatementContent = true;
                                currentStatementBytes += (c > 127) ? 2 : 1;
                                if (c == '"')
                                {
                                    if (i + 1 < len && line[i + 1] == '"')
                                    {
                                        i++; // 跳過 ""
                                        currentStatementBytes++;
                                    }
                                    else
                                    {
                                        inDoubleQuote = false;
                                    }
                                }
                                continue;
                            }

                            // 5. 符號開始判定
                            if (c == '-' && i + 1 < len && line[i + 1] == '-')
                            {
                                inLineComment = true;
                                i++;
                                continue;
                            }

                            if (c == '/' && i + 1 < len && line[i + 1] == '*')
                            {
                                blockCommentDepth = 1;
                                i++;
                                continue;
                            }

                            if (c == '\'')
                            {
                                inSingleQuote = true;
                                hasStatementContent = true;
                                currentStatementBytes += 1;
                                continue;
                            }

                            if (c == '"')
                            {
                                inDoubleQuote = true;
                                hasStatementContent = true;
                                currentStatementBytes += 1;
                                continue;
                            }

                            // 6. 語句結束判定
                            if (c == ';' && !inPlSqlBlock)
                            {
                                if (hasStatementContent)
                                {
                                    statementCount++;
                                    hasStatementContent = false;
                                    currentStatementBytes = 0;
                                }
                                continue;
                            }

                            if (!char.IsWhiteSpace(c))
                            {
                                hasStatementContent = true;
                            }

                            if (hasStatementContent)
                            {
                                currentStatementBytes += (c > 127) ? 2 : 1;
                                if (currentStatementBytes > MaxBatchSizeBytes)
                                {
                                    result.CanProceed = false;
                                    result.Issues.Add(string.Format("第 {0} 行單一語句大小已超過 32 MiB 安全上限。", lineNumber));
                                    return result;
                                }
                            }
                        }

                        inLineComment = false; // 換行結束單行註解
                    }

                    if (hasStatementContent)
                    {
                        statementCount++;
                    }

                    result.StatementCount = statementCount;

                    if (statementCount == 0)
                    {
                        result.Issues.Add("SQL 腳本中未發現任何有效的執行語句。");
                    }
                }
            }
            catch (Exception ex)
            {
                result.CanProceed = false;
                result.Issues.Add("預檢掃描檔案時發生例外錯誤: " + ex.Message);
            }

            return result;
        }

        private static Encoding DetectEncoding(string filePath)
        {
            byte[] bom = new byte[4];
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int read = fs.Read(bom, 0, 4);
                if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                    return Encoding.UTF8;
                if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                    return Encoding.Unicode; // UTF-16LE
                if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                    return Encoding.BigEndianUnicode; // UTF-16BE
                if (read >= 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xFE && bom[3] == 0xFF)
                    return Encoding.UTF32;
            }

            return new UTF8Encoding(false);
        }
    }
}
