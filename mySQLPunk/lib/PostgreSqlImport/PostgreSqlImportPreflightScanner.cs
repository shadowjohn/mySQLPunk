using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace mySQLPunk.lib.PostgreSqlImport
{
    public sealed class PostgreSqlImportPreflightScanner
    {
        private const int MaxBatchSizeBytes = 32 * 1024 * 1024; // 32 MiB
        private const int BufferSize = 65536; // 64KB

        private static readonly string[] PostgisKeywords = new[]
        {
            "geometry", "geography", "raster", "postgis", "spatial_ref_sys",
            "st_geomfromtext", "st_geomfromewkb", "st_astext", "st_asewkt",
            "st_point", "st_polygon", "st_buffer", "st_intersects", "st_dwithin",
            "addgeometrycolumn", "create extension postgis"
        };

        public async Task<PostgreSqlImportPreflightResult> ScanAsync(string filePath, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await Task.Run(() => Scan(filePath, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        public PostgreSqlImportPreflightResult Scan(string filePath, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty.", nameof(filePath));
            if (!File.Exists(filePath)) throw new FileNotFoundException("SQL script not found.", filePath);

            var result = new PostgreSqlImportPreflightResult();
            var fileInfo = new FileInfo(filePath);
            result.FileSize = fileInfo.Length;

            Encoding encoding = DetectEncoding(filePath);
            result.DetectedEncoding = encoding;

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            using (var reader = new StreamReader(fs, encoding, true, BufferSize))
            {
                int statementCount = 0;
                long currentStatementBytes = 0;
                bool hasStatementContent = false;
                bool inSingleQuote = false;
                bool inDoubleQuote = false;
                bool inLineComment = false;
                int blockCommentDepth = 0;
                string dollarTag = null; // 非 null 代表正在此特定 $tag$ 內

                var currentStmtSb = new StringBuilder();
                var spatialFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string line;
                int lineNumber = 0;

                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lineNumber++;

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

                    // 探測 PostGIS 關鍵字
                    string lowerLine = line.ToLowerInvariant();
                    foreach (var kw in PostgisKeywords)
                    {
                        if (lowerLine.Contains(kw))
                        {
                            result.HasPostgisFeatures = true;
                            if (spatialFound.Add(kw))
                            {
                                result.DetectedSpatialKeywords.Add(kw);
                            }
                        }
                    }

                    int len = line.Length;
                    for (int i = 0; i < len; i++)
                    {
                        char c = line[i];

                        // NUL byte 檢查
                        if (c == '\0')
                        {
                            result.CanProceed = false;
                            result.Issues.Add(string.Format("第 {0} 行包含二進位 NUL 空字元，檔案可能已損毀或非純文字 SQL 腳本。", lineNumber));
                            return result;
                        }

                        // 1. 若在單行註解中
                        if (inLineComment)
                        {
                            // 單行註解由換行符號終結
                            continue;
                        }

                        // 2. 若在塊註解中 (支援 PG 巢狀 /* /* */ */)
                        if (blockCommentDepth > 0)
                        {
                            if (c == '/' && i + 1 < len && line[i + 1] == '*')
                            {
                                blockCommentDepth++;
                                i++;
                            }
                            else if (c == '*' && i + 1 < len && line[i + 1] == '/')
                            {
                                blockCommentDepth--;
                                i++;
                            }
                            continue;
                        }

                        // 3. 若在單引號字串中
                        if (inSingleQuote)
                        {
                            hasStatementContent = true;
                            currentStatementBytes += (c > 127) ? 2 : 1;
                            if (c == '\'')
                            {
                                if (i + 1 < len && line[i + 1] == '\'')
                                {
                                    i++; // 跳過跳脫的 ''
                                    currentStatementBytes++;
                                }
                                else
                                {
                                    inSingleQuote = false;
                                }
                            }
                            continue;
                        }

                        // 4. 若在雙引號識別字中
                        if (inDoubleQuote)
                        {
                            hasStatementContent = true;
                            currentStatementBytes += (c > 127) ? 2 : 1;
                            if (c == '"')
                            {
                                if (i + 1 < len && line[i + 1] == '"')
                                {
                                    i++; // 跳過跳脫的 ""
                                    currentStatementBytes++;
                                }
                                else
                                {
                                    inDoubleQuote = false;
                                }
                            }
                            continue;
                        }

                        // 5. 若在 Dollar Quotes 中 ($tag$...$tag$)
                        if (dollarTag != null)
                        {
                            hasStatementContent = true;
                            currentStatementBytes += (c > 127) ? 2 : 1;
                            if (c == '$')
                            {
                                int tagLen = dollarTag.Length;
                                if (i + tagLen <= len && string.Compare(line, i, dollarTag, 0, tagLen, StringComparison.Ordinal) == 0)
                                {
                                    dollarTag = null; // 結束 Dollar Quote
                                    i += tagLen - 1;
                                    currentStatementBytes += tagLen - 1;
                                }
                            }
                            continue;
                        }

                        // 6. 一般狀態：檢查開始符號
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

                        // 探測開始 Dollar Quote
                        if (c == '$')
                        {
                            string potentialTag = TryMatchDollarTag(line, i);
                            if (potentialTag != null)
                            {
                                dollarTag = potentialTag;
                                result.HasDollarQuotes = true;
                                result.DollarQuoteBlocksCount++;
                                hasStatementContent = true;
                                currentStatementBytes += potentialTag.Length;
                                i += potentialTag.Length - 1;
                                continue;
                            }
                        }

                        // 分號終結語句
                        if (c == ';')
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

            return result;
        }

        private static string TryMatchDollarTag(string line, int startIndex)
        {
            if (startIndex >= line.Length || line[startIndex] != '$') return null;

            int end = -1;
            for (int i = startIndex + 1; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '$')
                {
                    end = i;
                    break;
                }
                // Tag 內部必須是合法的識別字元 (字母、數字、底線)
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return null;
                }
            }

            if (end > startIndex)
            {
                return line.Substring(startIndex, end - startIndex + 1);
            }

            return null;
        }

        private static Encoding DetectEncoding(string filePath)
        {
            byte[] bom = new byte[4];
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int read = fs.Read(bom, 0, 4);
                if (read >= 3 && bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf) return Encoding.UTF8;
                if (read >= 2 && bom[0] == 0xff && bom[1] == 0xfe) return Encoding.Unicode;
                if (read >= 2 && bom[0] == 0xfe && bom[1] == 0xff) return Encoding.BigEndianUnicode;
            }
            return new UTF8Encoding(false);
        }
    }
}
