using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace mySQLPunk.lib.MySqlImport
{
    public static class MySqlImportPreflightScanner
    {
        private const int MaxBatchBytes = 32 * 1024 * 1024; // 32 MiB
        private static readonly Regex ManifestTableRegex = new Regex(@"^--\s*Manifest-Table:\s*([^\s|]+)\s*\|\s*Rows:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ManifestTablesRegex = new Regex(@"^--\s*Tables:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ManifestRowsRegex = new Regex(@"^--\s*TotalRows:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ManifestSha256Regex = new Regex(@"^--\s*Manifest-SHA256:\s*([a-fA-F0-9]{64})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static MySqlImportPreflight Scan(string filePath, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path is required", nameof(filePath));
            if (!File.Exists(filePath))
                return new MySqlImportPreflight(false, 0, 0, false, 0, 0, null, new[] { "File not found: " + filePath });

            long fileBytes = new FileInfo(filePath).Length;
            var issues = new List<string>();
            int statementCount = 0;
            bool hasManifest = false;
            int manifestTables = 0;
            long manifestRows = 0;
            string manifestSha = null;

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                {
                    string delimiter = ";";
                    var currentBatch = new StringBuilder();
                    int lineIndex = 0;
                    string line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        lineIndex++;

                        // 檢查 embedded NUL (非法字元或編碼錯誤)
                        if (line.IndexOf('\0') >= 0)
                        {
                            issues.Add($"Line {lineIndex}: Contains Embedded NUL character (possible malformed encoding or binary data).");
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
                            continue;
                        }

                        // DELIMITER 宣告
                        if (trimmed.StartsWith("DELIMITER", StringComparison.OrdinalIgnoreCase))
                        {
                            string newDelim = trimmed.Substring(9).Trim();
                            if (!string.IsNullOrWhiteSpace(newDelim))
                            {
                                delimiter = newDelim;
                                currentBatch.Clear();
                                continue;
                            }
                        }

                        currentBatch.AppendLine(line);

                        // 單批次大小檢查 (32 MiB)
                        if (currentBatch.Length > MaxBatchBytes)
                        {
                            issues.Add($"Line {lineIndex}: Single SQL batch exceeds safe limit of 32 MiB.");
                            break;
                        }

                        // 檢查批次終止
                        if (trimmed.EndsWith(delimiter, StringComparison.Ordinal))
                        {
                            statementCount++;
                            currentBatch.Clear();
                        }
                    }

                    if (currentBatch.Length > 0 && !string.IsNullOrWhiteSpace(currentBatch.ToString()))
                    {
                        statementCount++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return new MySqlImportPreflight(false, fileBytes, statementCount, hasManifest, manifestTables, manifestRows, manifestSha, new[] { "Scan cancelled by user." });
            }
            catch (Exception ex)
            {
                issues.Add("Error during preflight scan: " + ex.Message);
            }

            bool passed = issues.Count == 0;
            return new MySqlImportPreflight(
                passed,
                fileBytes,
                statementCount,
                hasManifest,
                manifestTables,
                manifestRows,
                manifestSha,
                issues);
        }
    }
}
